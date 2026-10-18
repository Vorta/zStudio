using zStudio.Core.Worlds;

namespace zStudio.Core.Sources;

/// <summary>
/// A planned polygon zone stroke: the source changes (none when nothing changes), and per source polygon whether it
/// changed, already had (or lacked) the zone, or was full. <see cref="Full"/> names one selection of each full polygon.
/// </summary>
public sealed record SourcePolygonZoneStroke(string Label, IReadOnlyList<(string Relative, byte[] Content)> Changes,
    int Changed, int Unchanged, IReadOnlyList<SourceZoneSelection> Full);

public static partial class SourceZoneEdits
{
    /// <summary>Placements one stroke may reach; each is resolved once, however many of its polygons the stroke crosses.</summary>
    public const int MaximumStrokeOccurrences = 1024;

    /// <summary>
    /// The map's own model folder, the folder of the mission's master world model (<c>data/mN/models</c>): only models
    /// there take polygon zone edits. Models in <c>data/common</c> and in subfolders are shared or mission vehicles.
    /// </summary>
    public static string PaintableFolder(SourceWorkspace workspace, string mission, CancellationToken token = default)
        => Folder(MasterPath(workspace, mission, token));
    /// <summary>The mission's master world model (the database the script loads after GameGenSetWorld), which holds the zone labels.</summary>
    public static string MasterPath(SourceWorkspace workspace, string mission, CancellationToken token = default)
        => new SourceEmbeddedZones(workspace, token).TraceMission(mission).Master;
    /// <summary>The folder of a project path (<c>data/m1/models</c> for <c>data/m1/models/m1.gltf</c>).</summary>
    public static string FolderOf(string path) => Folder(SourceWorkspace.Normalize(path));

    /// <summary>The map's zone labels (Any included), read from its master model alone.</summary>
    public static IReadOnlyList<SourceMapZoneLabel> Labels(SourceWorkspace workspace, string mission, CancellationToken token = default)
        => new SourceEmbeddedZones(workspace, token).ReadMission(mission, includeEntries: false).Labels;

    /// <summary>
    /// The assignment site a node's polygons are painted at (its placement, or its file's own geometry), with its mesh: nodes
    /// with one site change together. Null for a node without source polygons.
    /// </summary>
    public static string? PolygonSite(WorldNodeProvenance origin) => origin.ModelFile is { } model && origin.ZoneMesh >= 0 && origin.Terrain == null
        ? Key(origin, model) + "#" + origin.ZoneMesh.ToString(System.Globalization.CultureInfo.InvariantCulture) : null;

    /// <summary>Null when the node's polygons take polygon zone edits; otherwise why they do not.</summary>
    public static string? PolygonZoneRefusal(SourceSceneState build, string folder, int sceneNode)
    {
        if (!build.Provenance.TryGetValue(sceneNode, out var origin))
            return "Units and pickups the mission places at run time take no polygon zones in the map.";
        if (origin.Terrain != null) return "Editable terrain keeps its zones in its recipe; change them with its terrain regions.";
        if (origin.ModelFile is not { } model || origin.ZonePolygons == null || origin.ZoneMesh < 0)
            return "This object has no source polygons to zone.";
        string own = Folder(SourceWorkspace.Normalize(model));
        if (!own.Equals(folder, StringComparison.OrdinalIgnoreCase))
            return $"{JsonData.ShownText(model, 160)} is not in the map's own folder ({folder}); only models there take polygon zones.";
        if (!Folder(SourceWorkspace.Normalize(origin.ZoneOwnerPath ?? model)).Equals(folder, StringComparison.OrdinalIgnoreCase))
            return $"{JsonData.ShownText(model, 160)} is placed by {JsonData.ShownText(origin.ZoneOwnerPath!, 160)}, outside the map's own folder ({folder}); only placements made there take polygon zones.";
        return null;
    }

    /// <summary>The zones a polygon zone word lists in order (255 is Any), or null for a word that is no plain list.</summary>
    public static byte[]? PolygonZoneIds(uint word)
    {
        int count = (byte)word;
        if (count > 3) return null;
        byte[] ids = new byte[count];
        for (int i = 0; i < count; i++) ids[i] = (byte)(word >> (8 * (i + 1)));
        return ids;
    }

    /// <summary>
    /// A polygon's zones after painting or erasing <paramref name="zone"/>, or null when painting a full polygon (three
    /// zones) skips it. A painted zone follows the others. Any stands alone, as on every shipped polygon that has it:
    /// painting Any replaces the other zones, and painting a numbered zone replaces Any. Erasing keeps the others' order;
    /// erasing the last zone leaves none, which differs from Any.
    /// </summary>
    public static IReadOnlyList<byte>? PaintedPolygonZones(IReadOnlyList<byte> zones, byte zone, bool erase)
    {
        if (erase) return zones.Contains(zone) ? zones.Where(z => z != zone).ToArray() : zones;
        if (zone == 255) return zones is [255] ? zones : [255];
        if (zones.Contains(zone)) return zones;
        var kept = zones.Where(z => z != 255).ToList();
        if (kept.Count >= 3) return null;
        kept.Add(zone);
        return kept;
    }

    /// <summary>
    /// Plans one stroke of the polygon zone tools: <paramref name="zone"/> painted on (or erased from) each selected source
    /// polygon, in the file that holds the occurrence's assignment. Only polygons of the map's own models take it; every
    /// placement of such a model in the map changes with it. Nothing is accepted or saved here.
    /// </summary>
    public static SourcePolygonZoneStroke PlanPolygonZones(SourceWorkspace workspace, SourceSceneState build,
        IReadOnlyList<SourceZoneSelection> selections, byte zone, bool erase, CancellationToken token = default)
    {
        if (selections.Count is < 1 or > MaximumSelections) throw Bad("A stroke takes between one and 4,096 polygons.");
        SourceEmbeddedZones sources = new(workspace, token);
        string folder = Folder(sources.TraceMission(build.Mission).Master);
        HashSet<string> files = new(StringComparer.OrdinalIgnoreCase);
        foreach (var selection in selections)
        {
            token.ThrowIfCancellationRequested();
            if (selection.Polygon == null) throw Bad("Polygon zones are painted on polygons; name a polygon of each target.");
            if (PolygonZoneRefusal(build, folder, selection.SceneNode) is { } refusal) throw Bad(refusal);
            var origin = build.Provenance[selection.SceneNode];
            files.Add(SourceWorkspace.Normalize(origin.ModelFile!)); files.Add(SourceWorkspace.Normalize(origin.ZoneOwnerPath ?? origin.ModelFile!));
        }
        // The map's own folder belongs to its mission: another mission's scripts loading one of these files would see the
        // edit too, and refuse the stroke.
        if (ForeignLoads(workspace, sources, build, folder, token).FirstOrDefault(load => files.Contains(load.Root)) is { Mission: not null } shared)
            throw Bad($"{shared.Mission} also loads {JsonData.ShownText(shared.Root, 160)}; polygon zones are painted only on models no other mission loads.");
        var context = Prepare(workspace, build, selections, token, MaximumStrokeOccurrences, scanMissions: false);
        int changed = 0, unchanged = 0;
        List<SourceZoneSelection> full = [];
        HashSet<SourceZoneTarget> counted = [];
        foreach (var group in context.Targets.GroupBy(Key, StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            var profile = context.Assets[group.Key].Profile;
            IReadOnlyList<uint>[]? meshes = null;
            Dictionary<int, uint[]> edited = [];
            // As PlanAssignments: a file default and an override that inherits it change together.
            var dependencies = (context.Assets[group.Key].AssignmentSites ?? [group.Key]).ToHashSet(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < context.Targets.Count; i++)
            {
                var target = context.Targets[i];
                if (!dependencies.Contains(Key(target))) continue;
                token.ThrowIfCancellationRequested();
                uint word = profile.MeshPolygons[target.Index][target.Polygon];
                var before = PolygonZoneIds(word);
                var after = before == null ? null : PaintedPolygonZones(before, zone, erase);
                bool own = Key(target).Equals(group.Key, StringComparison.OrdinalIgnoreCase) && counted.Add(target);
                if (before == null || after == null || after.SequenceEqual(before))
                {
                    if (own && after == null && before != null) full.Add(context.Selections[i]);
                    else if (own) unchanged++;
                    continue;
                }
                if (own) changed++;
                if (!edited.TryGetValue(target.Index, out var words))
                {
                    meshes ??= profile.MeshPolygons.ToArray();
                    words = profile.MeshPolygons[target.Index].ToArray();
                    edited.Add(target.Index, words); meshes[target.Index] = words;
                }
                words[target.Polygon] = ZoneWord(after);
            }
            if (meshes != null) context.Sources.Set(context.Assets[group.Key], profile with { MeshPolygons = meshes }, polygonsOnly: true);
        }
        string name = zone == 255 ? "Any" : $"zone {zone}";
        string polygons = changed == 1 ? "1 polygon" : $"{changed:N0} polygons";
        return new(erase ? $"Erase {name} from {polygons}" : $"Paint {name} on {polygons}",
            changed == 0 ? [] : context.Sources.Changes(), changed, unchanged, full);
    }

    private static readonly Dictionary<string, IReadOnlyList<(string Mission, string Root)>> foreignLoads = [];
    /// <summary>
    /// The models other missions' scripts load from <paramref name="folder"/> (traced; no model is read). Every mission
    /// script is an input of a world's build, so a result holds while the build's recorded scripts are the same. A mission
    /// whose scripts cannot be traced (one being authored) loads nothing here.
    /// </summary>
    private static IReadOnlyList<(string Mission, string Root)> ForeignLoads(SourceWorkspace workspace, SourceEmbeddedZones sources, SourceSceneState build, string folder, CancellationToken token)
    {
        var scripts = build.ReadHashes.Where(p => p.Key.StartsWith(SourceProject.GameGenFolder + "/", StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase).Select(p => p.Key + "=" + p.Value).ToArray();
        string? key = scripts.Length == 0 ? null : workspace.Root + "|" + build.Mission + "|" + folder + "|" + string.Join("|", scripts);
        lock (foreignLoads) if (key != null && foreignLoads.TryGetValue(key, out var known)) return known;
        List<(string Mission, string Root)> found = [];
        foreach (string other in Missions(workspace, token))
        {
            token.ThrowIfCancellationRequested();
            if (other.Equals(build.Mission, StringComparison.OrdinalIgnoreCase)) continue;
            IReadOnlyList<string> roots;
            try { roots = sources.TraceMission(other).Roots; }
            catch (InvalidDataException) { continue; }
            foreach (string root in roots)
                if (Folder(SourceWorkspace.Normalize(root)).Equals(folder, StringComparison.OrdinalIgnoreCase)) found.Add((other, SourceWorkspace.Normalize(root)));
        }
        if (key != null) lock (foreignLoads) { if (foreignLoads.Count >= 8) foreignLoads.Clear(); foreignLoads[key] = found; }
        return found;
    }

    private static string Folder(string path)
    {
        int slash = path.LastIndexOf('/');
        return slash < 0 ? "" : path[..slash];
    }
}
