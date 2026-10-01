namespace Recoil.Zbd.Core.Terrain;

/// <summary>
/// Edits of a terrain recipe, each returning the changed recipe. Every result is checked against the recipe rules by
/// writing and reading it back, so an edit can never leave a recipe the build would refuse.
/// </summary>
public static class TerrainEdits
{
    public static TerrainRecipe SetDefaults(TerrainRecipe recipe, TerrainAttributes defaults) => Checked(recipe with { Defaults = defaults });

    public static TerrainRecipe SetSurfaceDefaults(TerrainRecipe recipe, string id, TerrainAttributes defaults)
    {
        Surface(recipe, id);
        return Checked(recipe with { Surfaces = [.. recipe.Surfaces.Select(s => s.Id == id ? s with { Defaults = defaults } : s)] });
    }

    public static TerrainRecipe AddSurface(TerrainRecipe recipe, TerrainSurface surface) => Checked(recipe with { Surfaces = [.. recipe.Surfaces, surface] });

    /// <summary>Removes a surface and its mention in regions; a region that named only it is refused, since without a list it would cover every surface.</summary>
    public static TerrainRecipe RemoveSurface(TerrainRecipe recipe, string id)
    {
        Surface(recipe, id);
        if (recipe.Surfaces.Count == 1) throw new InvalidDataException("A recipe needs at least one surface.");
        if (recipe.Regions.FirstOrDefault(r => r.Surfaces.Count == 1 && r.Surfaces[0] == id) is { } only) throw new InvalidDataException($"Region {only.Name} applies only to {id}; remove or change it first.");
        return Checked(recipe with
        {
            Surfaces = [.. recipe.Surfaces.Where(s => s.Id != id)],
            Regions = [.. recipe.Regions.Select(r => r.Surfaces.Contains(id) ? r with { Surfaces = [.. r.Surfaces.Where(s => s != id)] } : r)],
        });
    }

    /// <summary>Adds a region at <paramref name="index"/> (the end when null); later regions override earlier ones.</summary>
    public static TerrainRecipe AddRegion(TerrainRecipe recipe, TerrainRegion region, int? index = null)
    {
        if (recipe.Regions.Any(r => r.Name == region.Name)) throw new InvalidDataException($"The recipe already has a region named {region.Name}.");
        int at = index ?? recipe.Regions.Count;
        if (at < 0 || at > recipe.Regions.Count) throw new InvalidDataException($"A region's place is 0–{recipe.Regions.Count}.");
        var shape = region.Shape is { } s ? s with { Polygons = TerrainShapes.Normalize(s.Polygons) } : null;
        return Checked(recipe with { Regions = [.. recipe.Regions.Take(at), region with { Shape = shape }, .. recipe.Regions.Skip(at)] });
    }

    public static TerrainRecipe UpdateRegion(TerrainRecipe recipe, string name, Func<TerrainRegion, TerrainRegion> change)
    {
        var region = Region(recipe, name);
        var changed = change(region);
        if (changed.Name != name && recipe.Regions.Any(r => r.Name == changed.Name)) throw new InvalidDataException($"The recipe already has a region named {changed.Name}.");
        return Checked(recipe with { Regions = [.. recipe.Regions.Select(r => r == region ? changed : r)] });
    }

    public static TerrainRecipe RemoveRegion(TerrainRecipe recipe, string name)
    {
        var region = Region(recipe, name);
        return Checked(recipe with { Regions = [.. recipe.Regions.Where(r => r != region)] });
    }

    /// <summary>Moves a region to <paramref name="index"/> in the order the regions apply.</summary>
    public static TerrainRecipe MoveRegion(TerrainRecipe recipe, string name, int index)
    {
        var region = Region(recipe, name);
        if (index < 0 || index >= recipe.Regions.Count) throw new InvalidDataException($"A region's place is 0–{recipe.Regions.Count - 1}.");
        var rest = recipe.Regions.Where(r => r != region).ToList();
        rest.Insert(index, region);
        return Checked(recipe with { Regions = rest });
    }

    /// <summary>
    /// Paints a stroke into a region's shape or erases it. A region that covers its whole surfaces (no shape) already holds
    /// every stroke; erasing from it leaves everywhere but the stroke. Erasing everything leaves an empty shape, which
    /// covers nothing, so the layers before it show through.
    /// </summary>
    public static TerrainRecipe Paint(TerrainRecipe recipe, string name, IReadOnlyList<TerrainOutline> stroke, bool add)
    {
        var region = Region(recipe, name);
        if (region.Shape == null && add) return recipe;
        var shape = region.Shape ?? new TerrainShape(TerrainShapes.Everywhere);
        return Checked(recipe with { Regions = [.. recipe.Regions.Select(r => r == region ? r with { Shape = shape with { Polygons = TerrainShapes.Paint(shape.Polygons, stroke, add) } } : r)] });
    }

    public static TerrainRegion Region(TerrainRecipe recipe, string name) => recipe.Regions.FirstOrDefault(r => r.Name == name) ?? throw new InvalidDataException($"The recipe has no region named {name}.");
    private static TerrainSurface Surface(TerrainRecipe recipe, string id) => recipe.Surfaces.FirstOrDefault(s => s.Id == id) ?? throw new InvalidDataException($"The recipe has no surface {id}.");
    /// <summary>The recipe as written and read back: the build's own rules decide whether it is valid.</summary>
    private static TerrainRecipe Checked(TerrainRecipe recipe) => TerrainRecipe.Parse(recipe.Write(), "The edited recipe");
}
