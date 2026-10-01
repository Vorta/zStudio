using System.IO;
using System.Numerics;
using System.Text.Json.Nodes;
using Recoil.Zbd.Automation;
using Recoil.Zbd.Core.Sources;
using Recoil.Zbd.Core.Terrain;
using Recoil.Zbd.Rendering;

namespace Recoil.Zbd.Desktop;

/// <summary>
/// Terrain in source worlds: recipes (see docs/source-project.md, Terrain) are edited through the project's workspace and
/// the world rebuilds with the new pieces. Properties of a terrain piece shows its recipe; the viewport brush paints the
/// selected region; MCP has the same edits as semantic commands (a stroke is a path and radius, never synthesized input).
/// </summary>
public partial class MainWindow
{
    /// <summary>The brush the viewport paints with in source worlds, or null when it is off.</summary>
    private TerrainBrushState? terrainBrush;

    private static SourceWorldSession SourceWorldOf(DocumentModel doc) => doc.SourceWorld ?? throw new StudioCommandException("unsupported", "This document is not a source world.");
    /// <summary>The project's recipes, those this world's pieces came from first.</summary>
    private static IReadOnlyList<string> TerrainRecipesOf(DocumentModel doc)
    {
        var used = doc.SourceBuild?.Provenance.Values.Select(p => p.Terrain).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray() ?? [];
        IReadOnlyList<string> all;
        try { all = SourceTerrain.Recipes(SourceWorldOf(doc).Workspace); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new StudioCommandException("io_failed", ex.Message); }
        return [.. used, .. all.Where(r => !used.Contains(r, StringComparer.OrdinalIgnoreCase))];
    }
    /// <summary>The mission database the world was built from: the glTF file its database nodes came from.</summary>
    private static string MissionDatabase(DocumentModel doc)
    {
        var files = doc.SourceBuild?.Provenance.Values.Where(p => p.Database).Select(p => p.ModelFile).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).Take(2).ToList() ?? [];
        return files.Count == 1 ? files[0] : throw new StudioCommandException("unsupported", files.Count == 0 ? "This world has no mission database (no load after GameGenSetWorld) to add terrain to." : "The mission database's nodes come from several files.");
    }
    private static TerrainRecipe ReadRecipe(DocumentModel doc, string recipe)
    {
        try { return SourceTerrain.Read(SourceWorldOf(doc).Workspace, recipe); }
        catch (InvalidDataException ex) { throw new StudioCommandException("invalid_argument", ex.Message); }
    }

    /// <summary>Changes a recipe as one undoable change and rebuilds the world; recipe edits read the current recipe, not the build, so they work in a world built before another's edit.</summary>
    private Task<DocumentModel> EditTerrainAsync(DocumentModel doc, string recipe, string label, Func<TerrainRecipe, TerrainRecipe> change, CancellationToken token) =>
        EditSourceWorldAsync(doc, label, w => SourceTerrain.Edit(w, recipe, label, change, token) is { } t ? () => w.Retract(t) : null, token, fromBuild: false);
    private Task<DocumentModel> CreateTerrainAsync(DocumentModel doc, string model, IReadOnlyList<string> nodes, string? recipe, CancellationToken token)
    {
        string database = MissionDatabase(doc);
        return EditSourceWorldAsync(doc, $"Creating terrain from {Path.GetFileName(model)}", w => SourceTerrain.Create(w, database, model, nodes, recipe, token) is var t ? () => w.Retract(t) : null, token, fromBuild: false);
    }
    private Task<DocumentModel> PaintTerrainAsync(DocumentModel doc, string recipe, string region, IReadOnlyList<Vector2> path, float radius, bool add, CancellationToken token)
    {
        IReadOnlyList<TerrainOutline> stroke;
        try { stroke = TerrainShapes.Stroke(path, radius); }
        catch (InvalidDataException ex) { throw new StudioCommandException("invalid_argument", ex.Message); }
        return EditTerrainAsync(doc, recipe, $"{(add ? "Paint" : "Erase")} {region}", r => TerrainEdits.Paint(r, region, stroke, add), token);
    }

    // ---------------------------------------------------------------- GUI

    /// <summary>Properties of a terrain: the recipe a piece came from, with the brush for its regions.</summary>
    private bool ShowTerrainProperties(DocumentModel doc, string recipe, string? surface, string? piece, string? region)
    {
        var parsed = ReadRecipe(doc, recipe);
        var window = GetPropertiesWindow();
        TerrainPropertiesEditor? fields = null;
        Task Follow(string label, Func<TerrainRecipe, TerrainRecipe> change, string? selectAfter = null) => FollowTerrainAsync(recipe, surface, selectAfter ?? fields?.SelectedRegion,
            () => EditTerrainAsync(doc, recipe, label, change, CancellationToken.None));
        fields = new(recipe, parsed, surface, piece, region, terrainBrush, new(
            SetDefaults: (s, a) => Follow(s == null ? "Change terrain defaults" : $"Change surface {s} defaults", r => s == null ? TerrainEdits.SetDefaults(r, a) : TerrainEdits.SetSurfaceDefaults(r, s, a)),
            // A renamed region stays selected under its new name.
            UpdateRegion: (name, change) => Follow($"Change region {name}", r => TerrainEdits.UpdateRegion(r, name, change), change(TerrainEdits.Region(parsed, name)).Name),
            AddRegion: name => Follow($"Add region {name}", r => TerrainEdits.AddRegion(r, new(name, [], new TerrainShape([]), TerrainAttributes.None)), name),
            RemoveRegion: name => Follow($"Delete region {name}", r => TerrainEdits.RemoveRegion(r, name)),
            MoveRegion: (name, index) => Follow($"Move region {name}", r => TerrainEdits.MoveRegion(r, name, index)),
            SelectRegion: name => ShowTerrainProperties(doc, recipe, surface, piece, name),
            SetBrush: brush => { SetTerrainBrush(brush); ShowTerrainProperties(doc, recipe, surface, piece, brush?.Region ?? fields?.SelectedRegion); }));
        bool opened = window.SetSourceObject(doc, fields);
        PresentProperties(window, opened);
        return opened;
    }
    /// <summary>Runs a recipe edit, then shows the same recipe of the rebuilt world in Properties.</summary>
    private async Task FollowTerrainAsync(string recipe, string? surface, string? region, Func<Task<DocumentModel>> edit)
    {
        var next = await edit();
        if (!next.IsDisposed && (propertiesWindow == null || propertiesWindow.Document == null || propertiesWindow.Document == next))
            try { ShowTerrainProperties(next, recipe, surface, null, region); } catch (StudioCommandException ex) { ViewModel.Status = ex.Message; }
    }
    /// <summary>Turns the viewport brush on (painting or erasing one region) or off.</summary>
    private void SetTerrainBrush(TerrainBrushState? brush)
    {
        terrainBrush = brush;
        ApplyTerrainBrush(scene);
        ViewModel.Status = brush == null ? "Terrain brush off." : $"{(brush.Add ? "Painting" : "Erasing")} {brush.Region}: drag over the terrain.";
    }
    private void ApplyTerrainBrush(SceneViewport? viewport)
    {
        if (viewport == null) return;
        bool on = terrainBrush != null && ViewModel.SelectedDocument?.SourceWorld != null;
        viewport.TerrainBrushRadius = terrainBrush?.Radius ?? 8;
        viewport.TerrainBrushActive = on;
    }
    private void ConfigureTerrainScene(SceneViewport viewport)
    {
        ApplyTerrainBrush(viewport);
        viewport.TerrainStrokeCompleted += stroke => _ = PaintStrokeAsync(stroke);
    }
    /// <summary>A viewport stroke: the brush's region gains (or loses) the area the brush covered, as one change.</summary>
    private async Task PaintStrokeAsync(IReadOnlyList<Vector3> stroke)
    {
        if (terrainBrush is not { } brush || ViewModel.SelectedDocument is not { SourceWorld: not null } doc) return;
        // Properties closes with the replaced document; when it showed this recipe, it shows it again for the rebuilt world.
        var shown = propertiesWindow?.SourceFields as TerrainPropertiesEditor;
        bool follow = shown?.RecipePath == brush.Recipe && propertiesWindow?.Document == doc;
        try
        {
            var next = await PaintTerrainAsync(doc, brush.Recipe, brush.Region, [.. stroke.Select(p => new Vector2(p.X, p.Z))], brush.Radius, brush.Add, CancellationToken.None);
            if (follow && !next.IsDisposed && (propertiesWindow == null || propertiesWindow.Document == null || propertiesWindow.Document == next))
                ShowTerrainProperties(next, brush.Recipe, shown!.Surface, null, brush.Region);
        }
        catch (StudioCommandException ex) { ViewModel.Status = ex.Message; }
    }

    /// <summary>GUI: Create terrain from meshes of a glTF file in the project, then show its recipe in Properties.</summary>
    private void CreateTerrainClick(object sender, System.Windows.RoutedEventArgs e) => _ = RunUi(async () =>
    {
        if (ViewModel.SelectedDocument is not { SourceWorld: { } session } doc) throw new StudioCommandException("unsupported", "Open a mission world of a source project first.");
        Microsoft.Win32.OpenFileDialog pick = new() { Title = "Choose the glTF file with the terrain surfaces", Filter = "glTF (*.gltf;*.glb)|*.gltf;*.glb", InitialDirectory = Path.Combine(session.Root, SourceProject.DataFolder) };
        if (pick.ShowDialog(this) != true) return;
        string full = Path.GetFullPath(pick.FileName), prefix = session.Root + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new StudioCommandException("invalid_argument", "Choose a file inside the source project.");
        string model = SourceProject.Relative(session.Root, full);
        IReadOnlyList<string> meshes;
        try { meshes = [.. SourceTerrain.MeshNodes(session.Workspace, model).Take(TerrainRecipe.MaximumSurfaces)]; }
        catch (InvalidDataException ex) { throw new StudioCommandException("invalid_argument", ex.Message); }
        if (meshes.Count == 0) throw new StudioCommandException("invalid_argument", $"{model} has no meshes.");
        TerrainCreateDialog dialog = new(model, meshes) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        var next = await CreateTerrainAsync(doc, model, dialog.Chosen, null, CancellationToken.None);
        string recipe = model[..model.LastIndexOf('.')] + TerrainRecipe.Extension;
        if (!next.IsDisposed) ShowTerrainProperties(next, recipe, null, null, null);
    });

    // ---------------------------------------------------------------- MCP

    private void RegisterSourceTerrainCommands(StudioCommands r)
    {
        Register(r, "source_terrain", "Describe the terrain recipes of the open source world's project (data/**/*.terrain.json): without recipe, each recipe with its surface and region counts and the pieces it compiled to in this world; with recipe, its surfaces, defaults and regions in the order they apply (name, surfaces, the attributes each sets, and its shape's parts, area and bounds); with region too, that region's shape coordinates (up to 4,096 points). Recipe attributes: zones, nodeZone, nodeGate, collision, standable, craters (allowed, blocked, ignored), soil, priority, flags.", false,
            [DocumentParameter, P("recipe", "string", "A recipe's project path, as listed."), P("region", "string", "A region of the recipe, for its shape's coordinates.")], a =>
        {
            var d = TargetDocument(a);
            var recipes = TerrainRecipesOf(d);
            var pieces = d.SourceBuild?.Provenance.Values.Where(p => p.Terrain != null).GroupBy(p => p.Terrain!, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase) ?? [];
            if (a["recipe"] is null)
                return Result(new
                {
                    document = d.SessionId, revision = d.Revision,
                    recipes = recipes.Take(64).Select(path =>
                    {
                        TerrainRecipe? recipe = null; string? error = null;
                        try { recipe = SourceTerrain.Read(SourceWorldOf(d).Workspace, path); } catch (InvalidDataException ex) { error = Bounded(ex.Message, 512); }
                        return new { path, surfaces = recipe?.Surfaces.Count, regions = recipe?.Regions.Count, pieces = pieces.GetValueOrDefault(path)?.Count ?? 0, error };
                    }).ToArray(),
                    recipeCount = recipes.Count
                });
            string path = Text(a, "recipe");
            var parsed = ReadRecipe(d, path);
            object? shape = null;
            if (a["region"] is not null)
            {
                var region = parsed.Regions.FirstOrDefault(x => x.Name == Text(a, "region")) ?? throw new StudioCommandException("invalid_argument", $"{path} has no region {Text(a, "region")}.");
                int budget = 4096;
                shape = region.Shape == null ? null : new
                {
                    polygons = region.Shape.Polygons.TakeWhile(p => (budget -= TerrainShapes.PointCount([p])) >= 0).Select(p => new
                    {
                        outer = p.Outer.Select(v => new[] { v.X, v.Y }).ToArray(),
                        holes = p.Holes.Select(h => h.Select(v => new[] { v.X, v.Y }).ToArray()).ToArray()
                    }).ToArray(),
                    polygonCount = region.Shape.Polygons.Count, minY = region.Shape.MinY, maxY = region.Shape.MaxY
                };
            }
            var built = pieces.GetValueOrDefault(path) ?? [];
            return Result(new
            {
                document = d.SessionId, revision = d.Revision, recipe = path, compiler = parsed.Compiler,
                defaults = parsed.Defaults.ToJson(),
                surfaces = parsed.Surfaces.Select(s => new { id = s.Id, model = s.Model, node = s.Node, defaults = s.Defaults.ToJson(), pieces = built.Count(p => p.TerrainSurface == s.Id) }).ToArray(),
                regions = parsed.Regions.Select((x, i) => new
                {
                    index = i, name = x.Name, surfaces = x.Surfaces, set = x.Set.ToJson(),
                    shape = x.Shape == null ? null : new
                    {
                        parts = x.Shape.Polygons.Count, points = TerrainShapes.PointCount(x.Shape.Polygons), area = TerrainShapes.SquareUnits(x.Shape.Polygons), minY = x.Shape.MinY, maxY = x.Shape.MaxY,
                        bounds = x.Shape.Polygons.Count == 0 ? null : new[] { x.Shape.Polygons.Min(p => p.Outer.Min(v => v.X)), x.Shape.Polygons.Min(p => p.Outer.Min(v => v.Y)), x.Shape.Polygons.Max(p => p.Outer.Max(v => v.X)), x.Shape.Polygons.Max(p => p.Outer.Max(v => v.Y)) }
                    }
                }).Take(256).ToArray(),
                regionCount = parsed.Regions.Count, regionShape = shape, pieces = built.Count,
                brush = terrainBrush?.Recipe == path ? new { region = terrainBrush.Region, mode = terrainBrush.Add ? "paint" : "erase", radius = terrainBrush.Radius } : null
            });
        });
        RegisterJob(r, "source_terrain_edit", "Edit a terrain recipe of the open source world's project as one undoable change of the workspace; the world rebuilds and the result is the replacement document. Actions: set_defaults (attributes; surface for a surface's defaults), add_region (region, optional surfaces, attributes, index; it starts empty unless wholeSurfaces), update_region (region; any of name, surfaces, attributes, wholeSurfaces), remove_region, move_region (region, index), paint (region, mode paint or erase, and a stroke: path of [x, z] points with radius, or polygons). Attributes patch the layer: a key with a value sets it, a key with null removes the override so the layers before show through, absent keys stay.",
            [DocumentParameter, RevisionParameter, P("recipe", "string", "The recipe's project path.", true),
             P("action", "string", "What to do.", true, ["set_defaults", "add_region", "update_region", "remove_region", "move_region", "paint"]),
             P("surface", "string", "set_defaults: the surface whose defaults change (the recipe's when omitted)."),
             P("region", "string", "The region's name."), P("name", "string", "update_region: a new name."),
             new("surfaces", "array", "Surfaces the region applies to (empty: all).", Items: new("", "string", "Surface id."), MaxItems: 256),
             new("attributes", "object", "Attribute patch: zones ([numbers] or \"any\"), nodeZone (number, \"any\", \"auto\"), nodeGate, collision, standable (booleans), craters (allowed, blocked, ignored), soil (name or 6–99), priority, flags (\"0x…\"); null removes an override."),
             P("wholeSurfaces", "boolean", "add_region/update_region: the region covers its whole surfaces (no shape)."),
             new("index", "integer", "add_region/move_region: the region's place in the order.", Minimum: 0, Maximum: TerrainRecipe.MaximumRegions),
             P("mode", "string", "paint: paint adds the stroke's area to the region, erase removes it.", false, ["paint", "erase"]),
             new("path", "array", "paint: the stroke's points as [x, z] pairs (a single point is a disc).", Items: new("", "array", "[x, z]."), MinItems: 1, MaxItems: 10_000),
             new("radius", "number", "paint: the brush radius in world units with path."),
             new("polygons", "array", "paint: polygons instead of a path, each { outer: [[x, z]…], holes: [[[x, z]…]] }.", Items: new("", "object", "A polygon."), MaxItems: TerrainRecipe.MaximumPolygons)], true,
            async (a, token) =>
            {
                var d = TargetDocument(a, true);
                string recipe = Text(a, "recipe"), action = Text(a, "action");
                string Region() => a["region"] is null ? throw new StudioCommandException("invalid_argument", "Give the region's name.") : Text(a, "region");
                TerrainAttributes Patch(TerrainAttributes current, string what)
                {
                    try { return TerrainAttributes.FromJson(a["attributes"], what, current); }
                    catch (InvalidDataException ex) { throw new StudioCommandException("invalid_argument", ex.Message); }
                }
                string[]? Surfaces() => (a["surfaces"] as JsonArray)?.Select(v => v is JsonValue s && s.TryGetValue(out string? id) ? id : throw new StudioCommandException("invalid_argument", "Surfaces are ids.")).ToArray();
                DocumentModel next;
                switch (action)
                {
                    case "set_defaults":
                        string? surface = a["surface"] is null ? null : Text(a, "surface");
                        next = await EditTerrainAsync(d, recipe, surface == null ? "Change terrain defaults" : $"Change surface {surface} defaults",
                            r => surface == null ? TerrainEdits.SetDefaults(r, Patch(r.Defaults, "attributes")) : TerrainEdits.SetSurfaceDefaults(r, surface, Patch(r.Surfaces.FirstOrDefault(s => s.Id == surface)?.Defaults ?? TerrainAttributes.None, "attributes")), token);
                        break;
                    case "add_region":
                        {
                            string name = Region(); int? index = a["index"] is null ? null : Int(a, "index");
                            bool whole = Flag(a, "wholeSurfaces");
                            next = await EditTerrainAsync(d, recipe, $"Add region {name}", r => TerrainEdits.AddRegion(r, new(name, Surfaces() ?? [], whole ? null : new TerrainShape([]), Patch(TerrainAttributes.None, "attributes")), index), token);
                            break;
                        }
                    case "update_region":
                        {
                            string name = Region();
                            next = await EditTerrainAsync(d, recipe, $"Change region {name}", r => TerrainEdits.UpdateRegion(r, name, x => x with
                            {
                                Name = a["name"] is null ? x.Name : Text(a, "name"),
                                Surfaces = Surfaces() ?? x.Surfaces,
                                Set = a["attributes"] is null ? x.Set : Patch(x.Set, "attributes"),
                                Shape = a["wholeSurfaces"] is null ? x.Shape : Flag(a, "wholeSurfaces") ? null : x.Shape ?? new TerrainShape([]),
                            }), token);
                            break;
                        }
                    case "remove_region": { string name = Region(); next = await EditTerrainAsync(d, recipe, $"Delete region {name}", r => TerrainEdits.RemoveRegion(r, name), token); break; }
                    case "move_region":
                        {
                            string name = Region();
                            if (a["index"] is null) throw new StudioCommandException("invalid_argument", "Give the region's new index.");
                            int index = Int(a, "index");
                            next = await EditTerrainAsync(d, recipe, $"Move region {name}", r => TerrainEdits.MoveRegion(r, name, index), token);
                            break;
                        }
                    case "paint":
                        {
                            string name = Region();
                            bool add = (a["mode"] is null ? "paint" : Text(a, "mode")) == "paint";
                            IReadOnlyList<TerrainOutline> stroke;
                            try
                            {
                                if (a["polygons"] is JsonArray polygons)
                                {
                                    var parsed = TerrainRecipe.Parse(System.Text.Encoding.UTF8.GetBytes(new JsonObject
                                    {
                                        ["format"] = TerrainRecipe.Format, ["version"] = TerrainRecipe.Version, ["compiler"] = TerrainRecipe.CurrentCompiler,
                                        ["surfaces"] = new JsonArray(new JsonObject { ["id"] = "s", ["model"] = "s.gltf", ["node"] = "s" }),
                                        ["regions"] = new JsonArray(new JsonObject { ["name"] = "stroke", ["shape"] = new JsonObject { ["polygons"] = polygons.DeepClone() }, ["set"] = new JsonObject() }),
                                    }.ToJsonString()), "polygons");
                                    stroke = parsed.Regions[0].Shape!.Polygons;
                                }
                                else
                                {
                                    var path = (a["path"] as JsonArray ?? throw new StudioCommandException("invalid_argument", "Give a path with a radius, or polygons.")).Select(p => p is JsonArray { Count: 2 } xz
                                        ? new Vector2(xz[0]!.GetValue<float>(), xz[1]!.GetValue<float>()) : throw new StudioCommandException("invalid_argument", "Path points are [x, z].")).ToArray();
                                    float radius = a["radius"] is JsonValue rv && rv.TryGetValue(out double rd) ? (float)rd : throw new StudioCommandException("invalid_argument", "Give the brush radius.");
                                    stroke = TerrainShapes.Stroke(path, radius);
                                }
                            }
                            catch (Exception ex) when (ex is InvalidDataException or FormatException or InvalidOperationException) { throw new StudioCommandException("invalid_argument", ex.Message); }
                            next = await EditTerrainAsync(d, recipe, $"{(add ? "Paint" : "Erase")} {name}", r => TerrainEdits.Paint(r, name, stroke, add), token);
                            break;
                        }
                    default: throw new StudioCommandException("invalid_argument", $"Unknown action {action}.");
                }
                return Result(new { document = DocumentState(next) });
            });
        RegisterJob(r, "source_terrain_create", "Create a terrain recipe for surfaces of a glTF file in the open source world's project (surfaces: node names, each with a mesh, in a file of their own — not the mission database) and add a marker for it at the end of the mission database's roots, as one undoable change; the world rebuilds with the compiled pieces. The recipe goes beside the file (name.terrain.json) unless recipe names another path ending in .terrain.json.",
            [DocumentParameter, RevisionParameter, P("model", "string", "The surfaces' glTF file (project path).", true),
             new("surfaces", "array", "Node names of the file that are terrain.", true, Items: new("", "string", "Node name."), MinItems: 1, MaxItems: TerrainRecipe.MaximumSurfaces),
             P("recipe", "string", "Optional recipe path.")], true,
            async (a, token) =>
            {
                var d = TargetDocument(a, true);
                var nodes = (a["surfaces"] as JsonArray)!.Select(v => v!.GetValue<string>()).ToArray();
                var next = await CreateTerrainAsync(d, Text(a, "model"), nodes, a["recipe"] is null ? null : Text(a, "recipe"), token);
                return Result(new { document = DocumentState(next), recipes = TerrainRecipesOf(next).Take(64).ToArray() });
            });
    }
}
