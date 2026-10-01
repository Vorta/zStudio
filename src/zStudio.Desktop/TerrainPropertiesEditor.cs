using System.Globalization;
using System.Text.Json.Nodes;
using System.Windows.Controls;
using Recoil.Zbd.Core.Terrain;

namespace Recoil.Zbd.Desktop;

/// <summary>What the brush paints: a recipe's region, painting or erasing, with a round brush of a radius in world units.</summary>
internal sealed record TerrainBrushState(string Recipe, string Region, bool Add, float Radius);

/// <summary>The edits the terrain editor makes; each changes the recipe as one undoable change and rebuilds the world.</summary>
internal sealed record TerrainEditorActions(
    Func<string?, TerrainAttributes, Task> SetDefaults,
    Func<string, Func<TerrainRegion, TerrainRegion>, Task> UpdateRegion,
    Func<string, Task> AddRegion,
    Func<string, Task> RemoveRegion,
    Func<string, int, Task> MoveRegion,
    Action<string> SelectRegion,
    Action<TerrainBrushState?> SetBrush);

/// <summary>
/// Properties of a terrain: the recipe's defaults, the shown piece's surface defaults and the regions in the order they
/// apply, with the selected region's attributes and the brush that paints it in the viewport. A piece itself is never
/// edited: its attributes come from these layers.
/// </summary>
internal sealed class TerrainPropertiesEditor : SourcePropertiesEditor
{
    private static readonly (string Key, string Label, string Hint)[] Fields =
    [
        ("zones", "Zones", "1 to 3 zone numbers, or any; empty uses the layers below"),
        ("nodeZone", "Node zone", "auto, any or a zone number"),
        ("nodeGate", "Zone gate", "on or off"),
        ("collision", "Collision", "on or off"),
        ("standable", "Standable", "on or off"),
        ("craters", "Craters", "allowed, blocked (no clip) or ignored"),
        ("soil", "Soil", "default, water, seafloor, quicksand, lava, fire or 6–99"),
        ("priority", "Draw priority", "0–255"),
        ("flags", "Node flags", "0x… (exact carried bits)"),
    ];
    private readonly string recipePath;
    private readonly TerrainRecipe recipe;
    private readonly string? surface, piece, selected;
    private readonly TerrainBrushState? brush;
    private readonly TerrainEditorActions actions;

    public TerrainPropertiesEditor(string recipePath, TerrainRecipe recipe, string? surface, string? piece, string? selected, TerrainBrushState? brush, TerrainEditorActions actions)
    {
        this.recipePath = recipePath; this.recipe = recipe; this.surface = surface; this.piece = piece; this.actions = actions;
        this.selected = selected != null && recipe.Regions.Any(r => r.Name == selected) ? selected : recipe.Regions.LastOrDefault()?.Name;
        this.brush = brush?.Recipe == recipePath && recipe.Regions.Any(r => r.Name == brush.Region) ? brush : null;
        Build();
    }
    public string RecipePath => recipePath;
    public string? SelectedRegion => selected;
    public string? Surface => surface;
    public override string Title => $"Terrain {System.IO.Path.GetFileName(recipePath)}";
    public override JsonObject Json => new()
    {
        ["recipe"] = recipePath, ["surface"] = surface, ["piece"] = piece, ["selectedRegion"] = selected,
        ["regions"] = new JsonArray(recipe.Regions.Select(r => (JsonNode?)JsonValue.Create(r.Name)).ToArray()),
        ["brush"] = brush == null ? null : new JsonObject { ["region"] = brush.Region, ["mode"] = brush.Add ? "paint" : "erase", ["radius"] = brush.Radius },
    };

    private void Build()
    {
        StackPanel form = new() { Margin = new(12) }; Content = new ScrollViewer { Content = form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Label(form, Title, true);
        Label(form, "Edits change the recipe and rebuild the world · Enter to apply · Escape to restore · an empty value uses the layers below");
        if (piece != null) ReadOnlyText(form, piece);

        Label(form, "Recipe defaults", true);
        Attributes(form, "Defaults", recipe.Defaults, a => actions.SetDefaults(null, a));
        if (surface != null && recipe.Surfaces.FirstOrDefault(s => s.Id == surface) is { } shown)
        {
            Label(form, $"Surface {shown.Id} ({shown.Node} in {shown.Model})", true);
            Attributes(form, $"Surface {shown.Id}", shown.Defaults, a => actions.SetDefaults(shown.Id, a));
        }

        Label(form, "Regions, in the order they apply", true);
        for (int i = 0; i < recipe.Regions.Count; i++)
        {
            var region = recipe.Regions[i]; int index = i;
            string reach = region.Shape == null ? "everywhere on its surfaces" : region.Shape.Polygons.Count == 0 ? "nothing painted yet"
                : $"{TerrainShapes.SquareUnits(region.Shape.Polygons):N0} square units in {region.Shape.Polygons.Count} parts";
            string sets = region.Set.ToJson().ToJsonString();
            StackPanel row = new() { Orientation = Orientation.Horizontal };
            AsyncButton(row, $"{index + 1}. {region.Name}" + (region.Name == selected ? " (selected)" : ""), () => { actions.SelectRegion(region.Name); return Task.CompletedTask; });
            if (index > 0) AsyncButton(row, $"Move {region.Name} up", () => actions.MoveRegion(region.Name, index - 1));
            if (index + 1 < recipe.Regions.Count) AsyncButton(row, $"Move {region.Name} down", () => actions.MoveRegion(region.Name, index + 1));
            AsyncButton(row, $"Delete {region.Name}", () => actions.RemoveRegion(region.Name));
            form.Children.Add(row);
            ReadOnlyText(form, $"{(region.Surfaces.Count == 0 ? "All surfaces" : string.Join(", ", region.Surfaces))} · {reach} · sets {sets}");
        }
        Input(form, "New region", "", _ => throw new InvalidOperationException("Use the asynchronous edit."), hint: "a name; the region starts empty and is painted with the brush",
            asyncCommit: async text => { text = text.Trim(); if (text.Length > 0) await actions.AddRegion(text); });

        if (selected != null && recipe.Regions.FirstOrDefault(r => r.Name == selected) is { } chosen)
        {
            Label(form, $"Region {chosen.Name}", true);
            Input(form, "Name", chosen.Name, _ => throw new InvalidOperationException("Use the asynchronous edit."),
                asyncCommit: async text => { text = text.Trim(); if (text != chosen.Name) await actions.UpdateRegion(chosen.Name, r => r with { Name = text }); });
            Input(form, "Surfaces", string.Join(", ", chosen.Surfaces), _ => throw new InvalidOperationException("Use the asynchronous edit."), hint: $"empty for all ({string.Join(", ", recipe.Surfaces.Select(s => s.Id))})",
                asyncCommit: async text =>
                {
                    string[] ids = text.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries);
                    if (!ids.SequenceEqual(chosen.Surfaces)) await actions.UpdateRegion(chosen.Name, r => r with { Surfaces = ids });
                });
            Attributes(form, $"Region {chosen.Name}", chosen.Set, a => actions.UpdateRegion(chosen.Name, r => r with { Set = a }));
            StackPanel shapeActions = new() { Orientation = Orientation.Horizontal };
            if (chosen.Shape != null) AsyncButton(shapeActions, "Cover whole surfaces", () => actions.UpdateRegion(chosen.Name, r => r with { Shape = null }));
            else AsyncButton(shapeActions, "Cover nothing (paint it)", () => actions.UpdateRegion(chosen.Name, r => r with { Shape = new TerrainShape([]) }));
            form.Children.Add(shapeActions);

            Label(form, "Brush", true);
            bool painting = brush?.Region == chosen.Name;
            float radius = brush?.Radius ?? 8;
            Input(form, "Brush radius", radius.ToString("R", CultureInfo.InvariantCulture), _ => throw new InvalidOperationException("Use the asynchronous edit."), hint: "world units",
                asyncCommit: text =>
                {
                    if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float value) || !float.IsFinite(value) || value < 0.01f || value > 100_000) throw new FormatException("Enter a radius from 0.01 to 100,000.");
                    actions.SetBrush(new(recipePath, chosen.Name, brush?.Add ?? true, value));
                    return Task.CompletedTask;
                });
            StackPanel brushActions = new() { Orientation = Orientation.Horizontal };
            AsyncButton(brushActions, painting && brush!.Add ? "Painting (stop)" : "Paint in viewport", () => { actions.SetBrush(painting && brush!.Add ? null : new(recipePath, chosen.Name, true, radius)); return Task.CompletedTask; });
            AsyncButton(brushActions, painting && !brush!.Add ? "Erasing (stop)" : "Erase in viewport", () => { actions.SetBrush(painting && !brush!.Add ? null : new(recipePath, chosen.Name, false, radius)); return Task.CompletedTask; });
            form.Children.Add(brushActions);
            if (painting) ReadOnlyText(form, $"Drag over the terrain to {(brush!.Add ? "paint" : "erase")} {chosen.Name}; each stroke is one undoable change. Escape drops a stroke in progress.");
        }
        RaiseChanged();
    }

    /// <summary>One input per attribute; committing one changes only it (empty removes the override).</summary>
    private void Attributes(StackPanel form, string what, TerrainAttributes current, Func<TerrainAttributes, Task> apply)
    {
        var json = current.ToJson();
        foreach (var (key, label, hint) in Fields)
        {
            string text = Display(json[key]);
            Input(form, $"{what}: {label}", text, _ => throw new InvalidOperationException("Use the asynchronous edit."), hint: hint,
                asyncCommit: async input =>
                {
                    input = input.Trim();
                    if (input == text) return;
                    var patch = new JsonObject { [key] = Value(key, input) };
                    await apply(TerrainAttributes.FromJson(patch, what, current));
                });
        }
    }
    private static string Display(JsonNode? node) => node switch
    {
        null => "",
        JsonArray list => string.Join(" ", list.Select(v => v!.ToString())),
        JsonValue v when v.TryGetValue(out bool b) => b ? "on" : "off",
        _ => node.ToString(),
    };
    /// <summary>Typed text as the recipe's JSON value: empty is null (no override).</summary>
    private static JsonNode? Value(string key, string text)
    {
        if (text.Length == 0) return null;
        switch (key)
        {
            case "zones":
                if (text.Equals("any", StringComparison.OrdinalIgnoreCase)) return "any";
                return new JsonArray(text.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries).Select(t => int.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out int z) ? (JsonNode?)z : throw new FormatException("Zones are numbers 0–254, or any.")).ToArray());
            case "nodeGate" or "collision" or "standable":
                return text.ToLowerInvariant() switch { "on" or "true" or "yes" => true, "off" or "false" or "no" => false, _ => throw new FormatException("Enter on or off.") };
            case "nodeZone" or "soil" or "priority":
                return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int n) ? n : text.ToLowerInvariant();
            default: return text.ToLowerInvariant().StartsWith("0x", StringComparison.Ordinal) ? text : text.ToLowerInvariant();
        }
    }
}
