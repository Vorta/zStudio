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
