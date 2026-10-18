using System.Globalization;
using System.Text.Json.Nodes;
using zStudio.Core;
using zStudio.Core.Worlds;

namespace zStudio.Desktop;

/// <summary>Read-only zone text shared by the inspection card (scene_inspect) and source-object Properties (source_world_object).</summary>
internal static class ZoneReadout
{
    /// <summary>A node zone word's ID, the low byte; 255 admits every zone.</summary>
    internal static string Zone(uint word) => (word & 0xFF) == 0xFF ? "Any (255)" : (word & 0xFF).ToString(CultureInfo.InvariantCulture);
    /// <summary>The built zone, as <c>Inherit (0)</c> when the source node inherits it (a stated number or any equals the built value).</summary>
    internal static string Zone(uint word, bool? inherited) => inherited == true ? $"Inherit ({Zone(word)})" : Zone(word);

    /// <summary>The card's plain form of a node zone: <c>Any</c> or the number, as <c>Inherit (7)</c> when inherited (no raw 255).</summary>
    internal static string ShortZone(uint word, bool? inherited)
    {
        string zone = (word & 0xFF) == 0xFF ? "Any" : (word & 0xFF).ToString(CultureInfo.InvariantCulture);
        return inherited == true ? $"Inherit ({zone})" : zone;
    }
    /// <summary>
    /// An inherited object zone, briefly: what it resolves to and the supplier's name, <c>Zone 3 · inherited from g101</c>
    /// (<see cref="Source"/> gives the full source).
    /// </summary>
    internal static string InheritedHint(uint word, WorldNodeProvenance origin)
    {
        string zone = (word & 0xFF) == 0xFF ? "Any" : "Zone " + (word & 0xFF).ToString(CultureInfo.InvariantCulture);
        string from = origin.ImportedZoneSupplier is not { } supplier ? "its parent"
            : supplier.ModelFile == null && supplier.LoadedFile != null ? "the load root"
            : supplier.ModelNodeName is { } name ? JsonData.ShownText(name, 160) : "its parent";
        return $"{zone} · inherited from {from}";
    }
    /// <summary>
    /// scene_inspect's <c>Object polygon zones</c> as lines in the card's wording ("Zone 7: 89 polygons", "Zones 7, 9: 14
    /// polygons", "Any: 3 polygons", "No zones: 2 polygons"), for cards without the source-object Zone section; null for
    /// another value.
    /// </summary>
    internal static string? PolygonZoneLines(JsonNode? value)
    {
        if (value is not JsonObject { } data || data["rows"] is not JsonArray rows) return null;
        static long Number(JsonNode? node) => node is JsonValue v ? v.TryGetValue<long>(out long wide) ? wide : v.TryGetValue<int>(out int narrow) ? narrow : 0 : 0;
        static string Polygons(long count) => count == 1 ? "1 polygon" : count.ToString("N0", CultureInfo.CurrentCulture) + " polygons";
        string? kind = data["truncated"] is JsonValue truncated && truncated.TryGetValue<string>(out var stopped) ? stopped : null;
        if (rows.Count == 0) return kind == null ? "No polygons here or below" : $"No polygons among the first {4096.ToString("N0", CultureInfo.CurrentCulture)} objects";
        List<string> lines = [];
        foreach (var row in rows.OfType<JsonObject>())
        {
            string name;
            if (row["moreSets"] is JsonValue) { long sets = Number(row["moreSets"]); name = sets == 1 ? "1 more zone set" : $"{sets.ToString("N0", CultureInfo.CurrentCulture)} more zone sets"; }
            else if (row["unknown"] is JsonValue) name = "Unknown zones";
            else
            {
                int[] zones = row["zones"] is JsonArray ids ? [.. ids.Select(id => (int)Number(id))] : [];
                name = zones.Length == 0 ? "No zones" : zones is [255] ? "Any"
                    : (zones.Length == 1 ? "Zone " : "Zones ") + string.Join(", ", zones.Select(z => z == 255 ? "Any" : z.ToString(CultureInfo.InvariantCulture)));
            }
            lines.Add($"{name}: {Polygons(Number(row["polygons"]))}");
        }
        if (rows.Count >= 2) lines.Add($"Total: {Number(data["total"]).ToString("N0", CultureInfo.CurrentCulture)}{(kind != null ? "+" : "")} polygons");
        if (kind != null)
            lines.Add(kind == "objects" ? $"(counted the first {4096.ToString("N0", CultureInfo.CurrentCulture)} objects only)" : $"(counted the first {200_000.ToString("N0", CultureInfo.CurrentCulture)} polygons only)");
        return string.Join('\n', lines);
    }
    /// <summary>A polygon's stored zone IDs (255 is Any; empty has no zones), or null when the set is unreadable.</summary>
    internal static int[]? PolygonZoneIds(JsonNode? set)
        => set is JsonArray ids ? [.. ids.Select(id => (int)JsonData.Scalar(id) & 0xFF)] : null;

    /// <summary>
    /// A polygon's zone set as the GameZ reader decodes it (one entry per counted ID); an empty set has no zones and passes
    /// every test, unlike an explicit Any.
    /// </summary>
    internal static string? PolygonZones(JsonNode? set)
    {
        if (set is not JsonArray ids) return null;
        if (ids.Count == 0) return "None (no zone information)";
        // The reader decodes IDs as signed bytes: Any (255) arrives as -1.
        return string.Join(", ", ids.Select(id => Zone(unchecked((uint)(int)JsonData.Scalar(id)))));
    }

    /// <summary>
    /// Where an imported node's zone comes from: the node itself, a reference placement's override, the ancestor it inherits
    /// from or the load root. Null where the build recorded none (a node a script created).
    /// </summary>
    internal static string? Source(WorldNodeProvenance origin)
    {
        if (origin.Terrain != null)
            return $"Terrain recipe {JsonData.ShownText(origin.Terrain, 512)}" + (origin.TerrainSurface is { } surface ? $" · surface {JsonData.ShownText(surface, 160)}" : "");
        if (origin.ModelFile == null && origin.LoadedFile != null) return Load(origin);
        if (origin.ImportedZoneInherited is not { } inherited) return null;
        if (!inherited) return "Assigned on this node · " + Node(origin);
        return origin.ImportedZoneSupplier is { } supplier
            ? "Inherited from " + (supplier.ModelFile == null && supplier.LoadedFile != null ? Load(supplier) : Node(supplier))
            : "Inherited from its parent";
    }

    private static string Node(WorldNodeProvenance origin)
    {
        string text = (origin.ModelNodeName is { } name ? JsonData.ShownText(name, 160) + " · " : "")
            + (origin.ModelFile is { } file ? JsonData.ShownText(file, 512) + $" · glTF node {origin.ModelNode}" : "a node a script created");
        return origin.ImportedZonePlacement is { } placement
            ? text + $" · assigned by the reference at glTF node {placement.Node} of {JsonData.ShownText(placement.File, 512)}" : text;
    }

    private static string Load(WorldNodeProvenance root)
        => "the load root of " + JsonData.ShownText(root.LoadedFile!, 512)
            + (root.Created is { } load ? $" · {JsonData.ShownText(load.Script, 256)} line {load.Line}" : "");
}
