using zStudio.Core;
using zStudio.Core.Animation;
using zStudio.Core.Sources;

namespace zStudio.Desktop;

/// <summary>
/// A zone of the shown map: the polygons that carry it as placed (a model placed twice counts twice), the placements and
/// models those are on, the objects and geometry whose own or inherited zone it is, and its label.
/// </summary>
internal sealed record SourceZoneRow(byte Zone, string? Label, long Polygons, int Placements, int Models, int OwnObjects, int InheritedObjects, int EmptyObjects = 0)
{
    internal int Objects => OwnObjects + InheritedObjects;
    internal bool Used => Polygons > 0 || Objects > 0;
}

/// <summary>
/// The World tree's sections beyond the scene, as the shown map has them: its zones, AI networks and animation definitions,
/// with the objects each definition binds to (tree rows). Read when a scene is shown; an edit shows another scene.
/// </summary>
internal sealed class SourceMapSections
{
    internal required string Mission { get; init; }
    internal required IReadOnlyList<SourceZoneRow> Zones { get; init; }
    /// <summary>Polygons with no zone (not a zone: the overlay leaves them untinted).</summary>
    internal long NoZonePolygons { get; init; }
    internal required IReadOnlyList<AiNetwork> Networks { get; init; }
    internal required SourceAnimationDefinitions Definitions { get; init; }
    /// <summary>The tree rows each definition binds to in this world, by definition index (none when it binds to nothing).</summary>
    internal required IReadOnlyDictionary<int, int[]> DefinitionRoots { get; init; }
    internal SourceZoneRow? Zone(byte id) => Zones.FirstOrDefault(z => z.Zone == id);
    internal AiNetwork? Network(string id) => Networks.FirstOrDefault(n => n.Id == id);

    /// <summary>A network's number, from its file name (net_05.zrd is 5); networks without one sort last.</summary>
    internal static int NetworkNumber(AiNetwork network)
    {
        string stem = System.IO.Path.GetFileNameWithoutExtension(network.Member);
        return stem.StartsWith("net_", StringComparison.OrdinalIgnoreCase) && int.TryParse(stem.AsSpan(4), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int number) ? number : int.MaxValue;
    }
    internal static string NetworkStem(AiNetwork network) => System.IO.Path.GetFileNameWithoutExtension(network.Member);

    /// <summary>
    /// The definitions section's rows: the mission's files, then the common ones (data/common), each in the order the lists
    /// read them. A file with two or more definitions is a row holding them; one with a single definition is that definition
    /// (the file in its text); a listed file that is missing is a row of its own.
    /// </summary>
    internal IReadOnlyList<SectionEntry> DefinitionScopes()
    {
        Dictionary<int, List<SourceAnimationDefinition>> byListing = [];
        foreach (var definition in Definitions.Definitions)
        {
            if (!byListing.TryGetValue(definition.Listing, out var list)) byListing[definition.Listing] = list = [];
            list.Add(definition);
        }
        List<SectionEntry> own = [], common = [];
        int ownCount = 0, commonCount = 0;
        foreach (var listing in Definitions.Listings)
        {
            // A missing file keeps the path as listed (relative to gamegen, with either slash): a common one still groups as common.
            string file = listing.File.Replace('\\', '/');
            bool shared = file.StartsWith("data/common/", StringComparison.OrdinalIgnoreCase) || listing.Missing && file.Contains("data/common/", StringComparison.OrdinalIgnoreCase);
            var into = shared ? common : own;
            if (listing.Missing) { into.Add(new MissingFileEntry(listing)); continue; }
            if (!byListing.TryGetValue(listing.Index, out var definitions) || definitions.Count == 0) continue;
            if (shared) commonCount += definitions.Count; else ownCount += definitions.Count;
            var rows = definitions.Select(d => new DefinitionEntry(d, listing, definitions.Count == 1, DefinitionRoots.GetValueOrDefault(d.Index)?.Length ?? 0, Definitions.BindingKnown)).ToArray();
            if (rows.Length == 1) into.Add(rows[0]);
            else into.Add(new DefinitionFileEntry(listing, rows));
        }
        List<SectionEntry> scopes = [];
        if (own.Count > 0) scopes.Add(new DefinitionScopeEntry(Mission, "data/" + Mission, own, ownCount));
        if (common.Count > 0) scopes.Add(new DefinitionScopeEntry("Common", "data/common", common, commonCount));
        return scopes;
    }
}

/// <summary>A World tree row that is no scene object: a section header, a zone, an AI network, an animation definition or a note.</summary>
internal abstract record SectionEntry(string Key);
/// <summary>The Zones, AI Networks or Animations section.</summary>
internal sealed record SectionHeader(string Section) : SectionEntry("section:" + Section);
internal sealed record ZoneEntry(SourceZoneRow Zone) : SectionEntry("zone:" + Zone.Zone.ToString(System.Globalization.CultureInfo.InvariantCulture));
/// <summary>Polygons without a zone: listed for information (not a zone, not selectable).</summary>
internal sealed record NoZonesEntry(long Polygons) : SectionEntry("zone:none");
internal sealed record NetworkEntry(AiNetwork Network) : SectionEntry("network:" + Network.Id);
internal sealed record DefinitionScopeEntry(string Name, string Folder, IReadOnlyList<SectionEntry> Items, int Count) : SectionEntry("scope:" + Name);
internal sealed record DefinitionFileEntry(SourceAnimationListing Listing, IReadOnlyList<DefinitionEntry> Definitions)
    : SectionEntry($"file:{Listing.File}#{Listing.Occurrence.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
/// <summary>A definition: its identity is its file, its position there and which listing of the file read it (never its name).</summary>
internal sealed record DefinitionEntry(SourceAnimationDefinition Definition, SourceAnimationListing Listing, bool Compact, int Roots, bool BindingKnown)
    : SectionEntry($"definition:{Definition.File}#{Definition.Ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture)}@{Listing.Occurrence.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
/// <summary>An exact compiled expansion of a source definition; opening it only reads source structure.</summary>
internal sealed record AnimationBindingEntry(DefinitionEntry Definition, AnimationStructureEntry Binding)
    : SectionEntry($"animationBinding:{Binding.Entry}");
/// <summary>Another bounded page of a definition's bindings.</summary>
internal sealed record AnimationPageEntry(DefinitionEntry Definition, int Offset)
    : SectionEntry($"animationPage:{Offset}");
internal sealed record MissingFileEntry(SourceAnimationListing Listing) : SectionEntry($"missing:{Listing.Index}:{Listing.File}");
/// <summary>A tertiary line in an empty section saying why it is empty.</summary>
internal sealed record NoteEntry(string Text) : SectionEntry("note");
/// <summary>Weapons, Ammo or Other (health and technology) under Pickups: always there, holding its pickups in list order.</summary>
internal sealed record PickupGroupEntry(string Group, IReadOnlyList<SourceMapPassEntry> Entries) : SectionEntry("pickups:" + Group);
/// <summary>With Group by type: one catalog type's pickups inside their group (a type never mixes kinds).</summary>
internal sealed record PickupTypeEntry(string Group, string Type, IReadOnlyList<SourceMapPassEntry> Entries) : SectionEntry("pickups:" + Group + ":" + Type);
