using System.Text.Json.Nodes;
using zStudio.Core.Worlds;

namespace zStudio.Core.Sources;

public static partial class SourceWorlds
{
    /// <summary>
    /// Prepares a source update that changed only polygon zones (and zone labels) without running GameGen: the built
    /// models whose polygons take another zone word are replaced, everything else is the accepted snapshot's. Each patched
    /// polygon must have carried the accepted sources' word, so a build that derived zones otherwise is never patched.
    /// A null result requires the normal source evaluator; no accepted data is mutated in either case. The caller still
    /// owns workspace dependency, external-change and revision checks before publication.
    /// </summary>
    public static SourceSceneSnapshot? TryUpdatePolygonZones(SourceSceneSnapshot accepted, IReadOnlyDictionary<string, byte[]> overlay,
        out string reason, CancellationToken token = default)
    {
        if (ReadChangedModels(accepted, overlay, token, out reason) is not { } read) return null;
        HashSet<string> changed = new(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, before, after) in read.Files)
        {
            token.ThrowIfCancellationRequested();
            if (!WorldGltf.PolygonZonesOnlyChanged(before, after, token)) { reason = "A changed model contains more than polygon zone edits."; return null; }
            changed.Add(SourceWorkspace.Normalize(path));
        }
        byte[]? Read(IReadOnlyDictionary<string, byte[]> models, string path, ProjectReadLimits limits) =>
            models.TryGetValue(path, out var bytes) ? bytes : read.Reads.Read(path, token, limits);
        SourceEmbeddedZones previous = new((path, limits) => Read(accepted.ModelSources, path, limits), token);
        SourceEmbeddedZones next = new((path, limits) => Read(read.Sources, path, limits), token);
        var nodes = GameZWriter.NodeSlots(accepted.World, token).ToDictionary(p => p.Value, p => p.Key);
        Dictionary<WorldModel, uint[]> patches = new(ReferenceEqualityComparer.Instance);
        HashSet<WorldNode> patched = new(ReferenceEqualityComparer.Instance);
        try
        {
            foreach (var (slot, origin) in accepted.Provenance)
            {
                token.ThrowIfCancellationRequested();
                if (origin.Terrain != null || origin.ModelFile is not { } model || origin.ZonePolygons is not { } mapping || origin.ZoneMesh < 0) continue;
                // An occurrence's words come from its model's defaults or an override along the files that reference it.
                bool reached = changed.Contains(SourceWorkspace.Normalize(model)) || origin.ZoneOwnerPath is { } owner && changed.Contains(SourceWorkspace.Normalize(owner));
                for (var at = origin.ReferencedBy; !reached && at != null; at = at.ReferencedBy)
                    reached = at.ModelFile is { } holder && changed.Contains(SourceWorkspace.Normalize(holder));
                if (!reached) continue;
                var oldWords = previous.Resolve(origin.ZoneOwnerPath ?? model, origin.ZoneOwnerReferences, model).Profile.MeshPolygons;
                var newWords = next.Resolve(origin.ZoneOwnerPath ?? model, origin.ZoneOwnerReferences, model).Profile.MeshPolygons;
                if (origin.ZoneMesh >= oldWords.Count || origin.ZoneMesh >= newWords.Count) { reason = "A changed occurrence no longer has its source mesh."; return null; }
                var (before, after) = (oldWords[origin.ZoneMesh], newWords[origin.ZoneMesh]);
                if (before.SequenceEqual(after)) continue;
                if (!nodes.TryGetValue(slot, out var node) || node.Model is not { } built || mapping.Count != built.Polygons.Count)
                { reason = "A changed occurrence has no built model of its source polygons."; return null; }
                uint[] zones = new uint[built.Polygons.Count];
                for (int i = 0; i < zones.Length; i++)
                {
                    uint current = built.Polygons[i].Zone;
                    int source = mapping[i];
                    bool known = source >= 0 && source < before.Count && source < after.Count;
                    if (known && current != before[source]) { reason = "A built polygon's zone does not come from its source word."; return null; }
                    zones[i] = known ? after[source] : current;
                }
                if (patches.TryGetValue(built, out var other) && !other.AsSpan().SequenceEqual(zones))
                { reason = "Placements that share one built model would take different polygon zones."; return null; }
                patches[built] = zones; patched.Add(node);
            }
        }
        catch (InvalidDataException ex) { reason = "The polygon zone sources could not be resolved: " + ex.Message; return null; }
        // A built model other nodes also use (one whose occurrence the edit did not reach) cannot change alone.
        foreach (var node in accepted.World.Nodes)
            if (node.Model is { } model && patches.ContainsKey(model) && !patched.Contains(node))
            { reason = "A built model with changed polygon zones is shared by an occurrence the edit did not reach."; return null; }
        // Occurrences of one source mesh whose zones agree share one built model; a patch that makes two of them agree
        // (zones painted back) would have the build join them.
        Dictionary<(string File, int Mesh), HashSet<WorldModel>> meshes = [];
        foreach (var (slot, origin) in accepted.Provenance)
        {
            if (origin.ModelFile is not { } file || origin.ZoneMesh < 0 || !nodes.TryGetValue(slot, out var node) || node.Model is not { } model) continue;
            var key = (SourceWorkspace.Normalize(file).ToUpperInvariant(), origin.ZoneMesh);
            if (!meshes.TryGetValue(key, out var models)) meshes[key] = models = new(ReferenceEqualityComparer.Instance);
            models.Add(model);
        }
        foreach (var models in meshes.Values)
        {
            if (models.Count < 2 || !models.Any(patches.ContainsKey)) continue;
            var zones = models.Select(model => patches.TryGetValue(model, out var patch) ? patch : model.Polygons.Select(p => p.Zone).ToArray()).ToArray();
            var listed = models.ToArray();
            for (int i = 0; i < listed.Length; i++)
            {
                token.ThrowIfCancellationRequested();
                if (!patches.ContainsKey(listed[i])) continue;
                for (int j = 0; j < listed.Length; j++)
                    if (j != i && zones[i].AsSpan().SequenceEqual(zones[j]))
                    { reason = "Placements of one model would take the same polygon zones again."; return null; }
            }
        }
        var world = CopyNodes(accepted.World, token);
        var scene = new GameScene();
        scene.Textures.AddRange(accepted.Scene!.Textures); scene.Materials.AddRange(accepted.Scene.Materials); scene.Nodes.AddRange(accepted.Scene.Nodes);
        scene.Models.AddRange(accepted.Scene.Models);
        Dictionary<WorldModel, WorldModel> replaced = new(ReferenceEqualityComparer.Instance);
        for (int index = 0; index < world.Models.Count; index++)
        {
            token.ThrowIfCancellationRequested();
            var model = world.Models[index];
            if (!patches.TryGetValue(model, out var zones) || model.Polygons.Select(p => p.Zone).SequenceEqual(zones)) continue;
            var copy = WithPolygonZones(model, zones);
            world.Models[index] = replaced[model] = copy;
            var shown = scene.Models[index];
            scene.Models[index] = shown with
            {
                Polygons = shown.Polygons.Select((polygon, i) => model.Polygons[i].Zone == zones[i] ? polygon
                    : polygon with { Metadata = WithZones(polygon.Metadata, zones[i], token) }).ToArray()
            };
        }
        foreach (var node in world.Nodes) if (node.Model is { } model && replaced.TryGetValue(model, out var copy)) node.Model = copy;
        reason = replaced.Count == 0 ? "Zone labels or unchanged polygon zones updated from the accepted source snapshot." : "Polygon zones updated from the accepted source snapshot.";
        return Derive(accepted, read, world, scene, null, token);
    }

    private static JsonObject WithZones(JsonObject metadata, uint word, CancellationToken token)
    {
        var copy = (JsonObject)JsonData.Clone(metadata, token)!;
        copy["zone_set"] = WorldScene.Zones(word);
        return copy;
    }

    /// <summary>A copy of <paramref name="model"/> whose polygons carry <paramref name="zones"/>; unchanged polygons are shared.</summary>
    private static WorldModel WithPolygonZones(WorldModel model, uint[] zones)
    {
        WorldModel copy = new() { Mode = model.Mode, Flags = model.Flags, MorphFactor = model.MorphFactor, ScrollU = model.ScrollU, ScrollV = model.ScrollV,
            ScrollFrame = model.ScrollFrame, BoundsCentre = model.BoundsCentre, BoundsRadius = model.BoundsRadius };
        copy.Vertices.AddRange(model.Vertices); copy.Normals.AddRange(model.Normals); copy.Morphs.AddRange(model.Morphs); copy.Points.AddRange(model.Points);
        for (int i = 0; i < model.Polygons.Count; i++)
        {
            var polygon = model.Polygons[i];
            copy.Polygons.Add(polygon.Zone == zones[i] ? polygon : new()
            { Flags = polygon.Flags, Priority = polygon.Priority, Material = polygon.Material, Zone = zones[i], Vertices = polygon.Vertices, Normals = polygon.Normals, Uvs = polygon.Uvs });
        }
        return copy;
    }
}
