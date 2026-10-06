using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Clipper2Lib;
using Recoil.Zbd.Core.Formats;
using Recoil.Zbd.Core.Gltf;
using Recoil.Zbd.Core.Terrain;
using Recoil.Zbd.Core.Worlds;

namespace Recoil.Zbd.Core.Sources;

/// <summary>A database root kept out of a conversion, and why.</summary>
public sealed record TerrainConversionKept(string Node, string Reason);
/// <summary>A planned surface: its id, node attributes and the database roots (glTF node indices, in root order) it merges.</summary>
public sealed record TerrainConversionSurface(string Id, uint Flags, int Zone, IReadOnlyList<int> Nodes)
{
    /// <summary>The pieces' model values (lighting, scrolling), which the surface's mesh carries.</summary>
    public JsonObject? ModelValues { get; init; }
}
/// <summary>What converting a mission database's pieces to editable terrain would do.</summary>
public sealed record TerrainConversionPlan(string Database, string Surfaces, string Recipe, IReadOnlyList<TerrainConversionSurface> Groups, IReadOnlyList<TerrainConversionKept> Kept)
{
    public int Converted => Groups.Sum(g => g.Nodes.Count);
}

/// <summary>
/// Convert to editable terrain: the mission database's untransformed mesh pieces become surfaces of a terrain recipe.
/// Pieces whose node attributes match merge into one surface, unless they overlap in plan view (the altitude probe
/// takes the first polygon of a node, so stacked sheets stay separate surfaces). Each surface keeps its pieces'
/// polygons, UVs, normals and materials (with their polygon zones and soils) exactly, and the recipe gives it the
/// pieces' exact node flags and zone. Pieces inside the database's groups (which the build deletes) count too; the parts
/// of the database in files of their own stay, since every copy of a part shares its file. Objects stay as they are:
/// the horizon and other landmarks, transformed nodes or nodes with children, references, shared nodes, and any piece
/// a script, resource or animation names (or matches by wildcard).
/// </summary>
public static partial class SourceTerrainConversion
{
    [GeneratedRegex(@"[A-Za-z0-9_\-\.\*%]+", RegexOptions.CultureInvariant)] private static partial Regex Word();
    /// <summary>
    /// The most distinct wildcard patterns the sources may hold (the releases' projects hold under 400 in all their files):
    /// planning matches every piece against each one.
    /// </summary>
    internal const int MaximumPatterns = 4096;

    /// <summary>
    /// Names and wildcard patterns (each * one digit) the given sources mention. A text source larger than the text-source
    /// limit (<see cref="SourceProject.MaximumSourceTextBytes"/>) is refused before it is decoded; a compiled resource is read
    /// as its decoded strings, never as text.
    /// </summary>
    public static (HashSet<string> Names, IReadOnlyList<Regex> Patterns) References(SourceWorkspace workspace, IEnumerable<string> files, CancellationToken token = default)
    {
        HashSet<string> names = new(StringComparer.Ordinal), wildcards = new(StringComparer.Ordinal); List<Regex> patterns = [];
        foreach (string file in files)
        {
            token.ThrowIfCancellationRequested();
            bool resource = file.EndsWith(".zrd", StringComparison.OrdinalIgnoreCase) || file.EndsWith(Animation.AnimationDefinitionSet.Extension, StringComparison.OrdinalIgnoreCase);
            bool keyframes = file.EndsWith(".zan", StringComparison.OrdinalIgnoreCase);
            if (!(resource || keyframes || file.EndsWith(".gs", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".gw", StringComparison.OrdinalIgnoreCase))) continue;
            if (workspace.Read(file, token) is not { } bytes) continue;
            // Every word, and also a resource's strings whole (names may hold spaces) and a script's tokens (names may hold
            // other characters): more names only keep more pieces as objects.
            if (resource && !ZrdText.LooksLikeText(bytes))
            {
                // Compiled zReader data names what its strings hold, as its text form would: their words in order, then each whole.
                ZrdNode tree;
                try { tree = ZrdDecoder.Read(bytes, token); }
                catch (InvalidDataException ex)
                {
                    // Not readable as zReader data: its words, as for any text, within the same limit.
                    if (bytes.Length > SourceProject.MaximumSourceTextBytes) throw new InvalidDataException($"{file} is not valid zReader data ({ex.Message}), so the names it holds are unknown.", ex);
                    Words(Encoding.Latin1.GetString(bytes));
                    continue;
                }
                foreach (string text in InOrder(tree)) { token.ThrowIfCancellationRequested(); Words(text); }
                foreach (string text in InOrder(tree)) Add(text);
                continue;
            }
            if (bytes.Length > SourceProject.MaximumSourceTextBytes)
                throw new InvalidDataException($"{file} exceeds {SourceProject.MaximumSourceTextBytes / (1024 * 1024)} MiB, the most a text source may hold, so the names it mentions are unknown.");
            string source = Encoding.Latin1.GetString(bytes);
            Words(source);
            if (resource)
            {
                try { foreach (string text in Strings(ZrdText.Parse(source, token))) Add(text); }
                catch (InvalidDataException) { }
            }
            else if (!keyframes)
                foreach (var line in GameGenScriptSyntax.Parse(source).Lines.Where(l => l.IsInstruction)) foreach (string t in line.Tokens) Add(t);
        }
        return (names, patterns);

        void Words(string text) { foreach (Match m in Word().Matches(text)) Add(m.Value); }
        // Each distinct pattern once.
        void Add(string t)
        {
            if (!t.Contains('*')) { names.Add(t); return; }
            if (!wildcards.Add(t)) return;
            if (wildcards.Count > MaximumPatterns)
                throw new InvalidDataException($"The sources hold more than {MaximumPatterns:N0} wildcard patterns, and every piece would be matched against each; terrain conversion is refused.");
            patterns.Add(new Regex("^" + Regex.Escape(t).Replace("\\*", "[0-9]") + "$", RegexOptions.CultureInvariant));
        }
        static IEnumerable<string> Strings(ZrdNode node)
        {
            Stack<ZrdNode> pending = new([node]);
            while (pending.TryPop(out var n)) { if (n.Kind == ZrdKind.String) yield return n.Text; foreach (var c in n.Children) pending.Push(c); }
        }
        // The tree's strings in the order its source lists them, holding only the path to each (a compiled tree may be wide).
        static IEnumerable<string> InOrder(ZrdNode root)
        {
            if (root.Kind == ZrdKind.String) { yield return root.Text; yield break; }
            Stack<(ZrdNode Node, int Next)> path = new([(root, 0)]);
            while (path.TryPop(out var at))
            {
                if (at.Next >= at.Node.Children.Count) continue;
                path.Push((at.Node, at.Next + 1));
                var child = at.Node.Children[at.Next];
                if (child.Kind == ZrdKind.String) yield return child.Text;
                else if (child.Children.Count > 0) path.Push((child, 0));
            }
        }
    }

    /// <summary>Plans converting <paramref name="database"/>'s pieces; names in <paramref name="references"/> stay objects.</summary>
    public static TerrainConversionPlan Plan(SourceWorkspace workspace, string database, (HashSet<string> Names, IReadOnlyList<Regex> Patterns) references, CancellationToken token = default)
    {
        var (root, doc) = Read(workspace, database, token);
        string stem = database[(database.LastIndexOf('/') + 1)..database.LastIndexOf('.')];
        string folder = database[..database.LastIndexOf('/')];
        string surfaces = $"{folder}/{stem}_terrain.gltf", recipe = $"{folder}/{stem}_terrain{TerrainRecipe.Extension}", buffer = $"{folder}/{stem}_terrain.bin";
        if (workspace.Exists(surfaces) || workspace.Exists(recipe) || workspace.Exists(buffer)) throw new InvalidDataException($"{surfaces}, {buffer} or {recipe} already exists; the database was converted before.");
        var json = (JsonArray)root["nodes"]!;
        List<TerrainConversionKept> kept = []; HashSet<string> shared = new(StringComparer.Ordinal);
        Dictionary<(uint Flags, int Zone, string Values), List<GltfNode>> groups = [];
        foreach (var (node, inherited) in Members(doc.Roots, 0xFF))
        {
            token.ThrowIfCancellationRequested();
            var extras = node.Extras?[WorldGltf.Key] as JsonObject;
            // A piece without a zone of its own takes its group's, as the importer gives it.
            int zone = Zone(extras, inherited);
            string name = WorldGltf.EngineName(node);
            var values = node.Mesh?.Extras?[WorldGltf.Key] as JsonObject;
            string? reason = extras?["terrain"] != null ? "terrain marker"
                : node.Mesh == null ? "no mesh of its own"
                : node.Children.Count > 0 ? "has children"
                : extras?["ref"] != null ? "references a model file"
                : extras?["instance"] != null ? "shared by several parents"
                : extras?["class"] is JsonValue c && c.ToString() == "lod" ? "level-of-detail node"
                : node.Matrix is { } m && !m.IsIdentity ? "placed with a transform"
                : node.Mesh.Weights.Count > 0 || node.Mesh.Primitives.Any(p => p.Targets.Count > 0) ? "has morph targets"
                : (Flags(extras) & 0x80) != 0 ? "landmark (the horizon and other always-drawn nodes)"
                : (Flags(extras) & 0x60) != 0 ? "collides by its bounding box or is a proximity node (both depend on the node's own bounds)"
                : values?["points"] is JsonArray { Count: > 0 } ? "holds point entries (lens flares)"
                : values?["mode"] is JsonValue mode && mode.ToString() != "0" ? "a facade or point model"
                : references.Names.Contains(name) ? "named by a script, resource or animation"
                : references.Patterns.FirstOrDefault(p => p.IsMatch(name)) is { } pattern ? $"matched by the wildcard {pattern}"
                : extras?["zoneWord"] is JsonValue word && word.ToString() is var w && zone is var z && !w.Equals($"0x{(uint)z:X}", StringComparison.OrdinalIgnoreCase) && !w.Equals($"0x{(uint)z:X8}", StringComparison.OrdinalIgnoreCase) ? "has a zone word beyond its zone"
                : null;
            if (reason != null) { kept.Add(new(name, reason)); continue; }
            var key = (Flags(extras), zone, values?.ToJsonString() ?? "");
            if (!groups.TryGetValue(key, out var list)) groups[key] = list = [];
            list.Add(node);
        }
        if (groups.Count > TerrainRecipe.MaximumSurfaces) throw new InvalidDataException($"The pieces would need at least {groups.Count} surfaces (different flags, zones or model values), more than a recipe's {TerrainRecipe.MaximumSurfaces}.");
        // Within a group, pieces that overlap in plan view go to different surfaces (first fit, in root order).
        List<TerrainConversionSurface> result = [];
        foreach (var ((flags, zone, values), nodes) in groups.OrderBy(g => g.Key.Zone).ThenBy(g => g.Key.Flags).ThenBy(g => g.Key.Values, StringComparer.Ordinal))
        {
            List<(List<GltfNode> Members, PathsD Area)> layers = [];
            foreach (var node in nodes)
            {
                token.ThrowIfCancellationRequested();
                var area = PlanArea(node);
                var layer = layers.FirstOrDefault(l => Math.Abs(Clipper.Area(Clipper.Intersect(l.Area, area, FillRule.NonZero, 3))) <= 0.01);
                if (layer.Members == null && result.Count + layers.Count >= TerrainRecipe.MaximumSurfaces)
                    throw new InvalidDataException($"The pieces would need more than a recipe's {TerrainRecipe.MaximumSurfaces} surfaces (different flags, zones, model values, or stacked sheets).");
                if (layer.Members == null) layers.Add(([node], area));
                else { layer.Members.Add(node); int at = layers.IndexOf(layer); layers[at] = (layer.Members, Clipper.Union(layer.Area, area, FillRule.NonZero, 3)); }
            }
            if (result.Count + layers.Count > TerrainRecipe.MaximumSurfaces) throw new InvalidDataException($"The pieces would need more than a recipe's {TerrainRecipe.MaximumSurfaces} surfaces (different flags, zones, model values, or stacked sheets).");
            // Pieces with model values of their own (an unlit or scrolling surface) are a surface of their own, named by a short hash of the values.
            string model = values.Length == 0 ? "" : "_m" + SourceProject.Sha256(Encoding.UTF8.GetBytes(values))[..6];
            for (int k = 0; k < layers.Count; k++)
                result.Add(new($"z{(zone == 0xFF ? "any" : zone.ToString(System.Globalization.CultureInfo.InvariantCulture))}_{flags:x8}{model}" + (layers.Count > 1 ? $"_{k + 1}" : ""), flags, zone, [.. layers[k].Members.Select(n => n.Index)])
                { ModelValues = values.Length == 0 ? null : JsonNode.Parse(values) as JsonObject });
        }
        return new(database, surfaces, recipe, result, kept);

        // The database's objects: its roots and, in their place, what its groups hold (a part's reference holds the
        // database's own records; the part's objects are in its file), with the zone each inherits (the roots' 0xFF).
        IEnumerable<(GltfNode Node, int Zone)> Members(IEnumerable<GltfNode> nodes, int inherited)
        {
            foreach (var node in nodes)
            {
                if (!WorldGltf.IsGroup(node, database)) { yield return (node, inherited); continue; }
                var extras = node.Extras?[WorldGltf.Key] as JsonObject;
                // A group several parents share is written once per parent and imported from its first copy alone: its
                // pieces would be converted once per copy, and the copies would no longer agree.
                if (extras?["instance"] != null) { if (shared.Add(extras["instance"]!.ToJsonString())) kept.Add(new(WorldGltf.EngineName(node), "a group several parents share")); continue; }
                if (extras?["ref"] != null) kept.Add(new(WorldGltf.EngineName(node), "a part of the mission database in a file of its own"));
                foreach (var member in Members(node.Children, Zone(extras, inherited))) yield return member;
            }
        }
    }

    /// <summary>Applies a plan as one change: the surfaces file, the recipe, and the database without the pieces and with the marker in the first piece's place.</summary>
    public static SourceTransaction Apply(SourceWorkspace workspace, TerrainConversionPlan plan, CancellationToken token = default)
    {
        if (plan.Groups.Count == 0) throw new InvalidDataException("No piece of the mission database can become terrain.");
        var (root, doc) = Read(workspace, plan.Database, token);
        var byIndex = doc.AllNodes().Where(n => n.Index >= 0).ToDictionary(n => n.Index);
        // The surfaces: one node per surface whose mesh holds its pieces' primitives as they were.
        GltfDocument surfaces = new();
        foreach (var group in plan.Groups)
        {
            GltfMesh mesh = new() { Name = group.Id, Extras = group.ModelValues == null ? null : new JsonObject { [WorldGltf.Key] = group.ModelValues.DeepClone() } };
            foreach (int index in group.Nodes)
            {
                if (!byIndex.TryGetValue(index, out var node) || node.Mesh == null) throw new InvalidDataException($"{plan.Database} changed since the conversion was planned.");
                mesh.Primitives.AddRange(node.Mesh.Primitives);
            }
            surfaces.Roots.Add(new GltfNode { Name = group.Id, Mesh = mesh });
        }
        string binary = plan.Surfaces[(plan.Surfaces.LastIndexOf('/') + 1)..^".gltf".Length] + ".bin";
        var (json, bin) = surfaces.Write(binary);
        string folder = plan.Surfaces[..plan.Surfaces.LastIndexOf('/')];
        // The recipe: each surface keeps its pieces' exact node flags and zone.
        TerrainRecipe recipe = new(TerrainRecipe.CurrentCompiler,
            [.. plan.Groups.Select(g => new TerrainSurface(g.Id, plan.Surfaces[(folder.Length + 1)..], g.Id, new() { Flags = g.Flags & WorldGltf.CarriedFlags, NodeZone = g.Zone == 0xFF ? TerrainAttributes.AnyZone : g.Zone }))],
            TerrainAttributes.None, []);
        // The database: the pieces leave; the marker stands among the roots where the first of them (or its group) stood.
        var nodes = (JsonArray)root["nodes"]!;
        var scenes = (JsonArray)root["scenes"]!;
        int sceneIndex = root["scene"] is JsonValue s && s.TryGetValue(out int si) ? si : 0;
        var sceneRoots = (JsonArray)scenes[sceneIndex]!["nodes"]!;
        var converted = plan.Groups.SelectMany(g => g.Nodes).ToHashSet();
        bool Holds(GltfNode node) => converted.Contains(node.Index) || node.Children.Any(Holds);
        int place = sceneRoots.Select(n => n!.GetValue<int>()).TakeWhile(n => !(byIndex.TryGetValue(n, out var node) && Holds(node))).Count();
        foreach (int index in converted.OrderDescending()) GltfNodeEdits.Remove(root, index);
        string stem = Path.GetFileName(plan.Recipe)[..^TerrainRecipe.Extension.Length];
        nodes.Add(new JsonObject { ["name"] = stem, ["extras"] = new JsonObject { [WorldGltf.Key] = new JsonObject { ["terrain"] = SourceTerrain.RelativePath(plan.Database, plan.Recipe) } } });
        // Removal renumbers the scene's list into a new array.
        ((JsonArray)((JsonArray)root["scenes"]!)[sceneIndex]!["nodes"]!).Insert(place, nodes.Count - 1);
        byte[] original = workspace.Read(plan.Database, token) ?? [];
        bool indented = original.AsSpan(0, Math.Min(original.Length, 4096)).Contains((byte)'\n');
        byte[] database = Encoding.UTF8.GetBytes(root.ToJsonString(new JsonSerializerOptions { WriteIndented = indented }));
        return workspace.Apply($"Convert {Path.GetFileName(plan.Database)} to editable terrain",
            [(plan.Surfaces, json), ($"{folder}/{binary}", bin), (plan.Recipe, TerrainRecipe.Parse(recipe.Write(), plan.Recipe).Write()), (plan.Database, database)], token)
            ?? throw new InvalidDataException("The conversion changed nothing.");
    }

    private static (JsonObject Root, GltfDocument Doc) Read(SourceWorkspace workspace, string database, CancellationToken token)
    {
        database = SourceWorkspace.Normalize(database);
        if (!database.EndsWith(".gltf", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"{database} must be a .gltf file to convert.");
        byte[] bytes = workspace.Read(database, token) ?? throw new InvalidDataException($"The project has no {database}.");
        var doc = GltfDocument.Read(bytes, uri => workspace.Read(WorldAssembler.Relative(database, uri), token) ?? throw new InvalidDataException($"{database} names {uri}, which does not exist."), token);
        JsonObject root;
        try { root = JsonNode.Parse(bytes, documentOptions: new() { MaxDepth = 256 }) as JsonObject ?? throw new InvalidDataException($"{database} is not a JSON object."); }
        catch (JsonException ex) { throw new InvalidDataException($"{database} is not valid JSON: {ex.Message}", ex); }
        return (root, doc);
    }
    private static uint Flags(JsonObject? extras) =>
        extras?["flags"] is JsonValue v && v.TryGetValue(out string? hex)
            && uint.TryParse(hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? hex.AsSpan(2) : hex.AsSpan(), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out uint flags) ? flags & WorldGltf.CarriedFlags : WorldGltf.DefaultCarried;
    /// <summary>A node's zone as the importer reads it (<see cref="WorldGltf.StatedZone"/>); without one, <paramref name="inherited"/>.</summary>
    private static int Zone(JsonObject? extras, int inherited = 0xFF) => WorldGltf.StatedZone(extras) is { } zone ? (int)zone : inherited;
    /// <summary>The plan-view area a node's triangles cover.</summary>
    private static PathsD PlanArea(GltfNode node)
    {
        PathsD triangles = [];
        foreach (var p in node.Mesh!.Primitives)
            for (int t = 0; t + 2 < p.Indices.Count; t += 3)
            {
                Vector3 a = p.Positions[p.Indices[t]], b = p.Positions[p.Indices[t + 1]], c = p.Positions[p.Indices[t + 2]];
                // One orientation for all, so the nonzero union never cancels opposite windings.
                double cross = (b.X - (double)a.X) * (c.Z - (double)a.Z) - (b.Z - (double)a.Z) * (c.X - (double)a.X);
                if (Math.Abs(cross) < 1e-9) continue;
                triangles.Add(cross > 0 ? [new PointD(a.X, a.Z), new PointD(b.X, b.Z), new PointD(c.X, c.Z)] : [new PointD(a.X, a.Z), new PointD(c.X, c.Z), new PointD(b.X, b.Z)]);
            }
        return Clipper.Union(triangles, FillRule.NonZero);
    }
}
