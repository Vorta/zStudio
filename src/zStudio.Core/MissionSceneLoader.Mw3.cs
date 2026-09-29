using System.Numerics;
using System.Text.Json.Nodes;
using Recoil.Zbd.Core.Formats;

namespace Recoil.Zbd.Core;

public sealed record MissionVariant(string Archive, string Label);
/// <summary>Authored mission readers plus per-file discovery failures; one unreadable archive never hides the others.</summary>
public sealed record Mw3MissionCatalog(IReadOnlyList<MissionVariant> Missions, IReadOnlyList<string> Diagnostics)
{
    /// <summary>Readers that could not be opened; their authored content is unknown, not absent.</summary>
    public IReadOnlySet<string> Unreadable { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
}

public static partial class MissionSceneLoader
{
    internal sealed record Mw3Resources(string? Mission, string[] Files, string? Unavailable, IReadOnlyList<string> Diagnostics, bool Unreadable = false);
    // Discovery, loading and valve scopes share one definition of a mission reader.
    private static bool IsMissionAiv(AssetRecord asset) => asset.Kind == AssetKind.Zrd && asset.Name.Equals("aiv.zrd", StringComparison.OrdinalIgnoreCase);
    public static async Task<IReadOnlyList<MissionVariant>> Mw3MissionsAsync(string worldPath, AssetResolver resolver, CancellationToken token = default)
        => (await Mw3MissionCatalogAsync(worldPath, resolver, token).ConfigureAwait(false)).Missions;
    public static async Task<Mw3MissionCatalog> Mw3MissionCatalogAsync(string worldPath, AssetResolver resolver, CancellationToken token = default)
    {
        List<MissionVariant> result = []; PreviewNotes diagnostics = new(); HashSet<string> unreadable = new(StringComparer.OrdinalIgnoreCase);
        foreach (string path in Directory.EnumerateFiles(Path.GetDirectoryName(Path.GetFullPath(worldPath))!, "*.zbd").Order(StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (FormatRegistry.Probe(path).Family != FormatFamily.Archive) continue;
                var doc = await resolver.OpenCachedAsync(path, token).ConfigureAwait(false);
                if (!doc.Assets.Any(IsMissionAiv)) continue;
                string name = Path.GetFileNameWithoutExtension(path);
                string label = name.StartsWith("readermp", StringComparison.OrdinalIgnoreCase) ? "Multiplayer " + name[8..] :
                    name.StartsWith("readeria", StringComparison.OrdinalIgnoreCase) ? "Instant action " + name[8..] :
                    name.StartsWith("readerm", StringComparison.OrdinalIgnoreCase) ? "Campaign " + name[7..] : name;
                result.Add(new(Path.GetFullPath(path), label));
            }
            // Match the RECOIL loader: report the file and keep every other reader available.
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
            { diagnostics.Add($"Mission reader {Path.GetFileName(path)}: {ex.Message}"); unreadable.Add(Path.GetFullPath(path)); }
        }
        return new(result.OrderBy(m => m.Label.StartsWith("Campaign", StringComparison.Ordinal) ? 0 : 1).ThenBy(m => m.Archive, StringComparer.OrdinalIgnoreCase).ToArray(), diagnostics) { Unreadable = unreadable };
    }
    /// <summary>
    /// Resolve the requested reader once. A remembered selection that no longer qualifies falls
    /// back to the first reader with a diagnostic; an exact request fails instead of loading another.
    /// </summary>
    internal static async Task<Mw3Resources> Mw3ResourcesAsync(string worldPath, AssetResolver resolver, string? requested, bool exact, CancellationToken token)
    {
        var catalog = await Mw3MissionCatalogAsync(worldPath, resolver, token).ConfigureAwait(false);
        var choices = catalog.Missions; string? unavailable = null; bool unreadable = false;
        string? selected = requested == null ? null : choices.FirstOrDefault(c => c.Archive.Equals(Path.GetFullPath(requested), StringComparison.OrdinalIgnoreCase))?.Archive;
        if (requested != null && selected == null)
        {
            if (exact) throw new InvalidDataException($"The mission reader {Path.GetFileName(requested)} is no longer available. Choose another mission.");
            unavailable = Path.GetFullPath(requested); unreadable = catalog.Unreadable.Contains(unavailable);
        }
        selected ??= choices.FirstOrDefault()?.Archive;
        var missionFiles = choices.Select(c => c.Archive).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Only the chosen mission contributes its resources; shared map/root archives follow it.
        // Unreadable readers are neither the chosen mission nor shared resources; the catalog already reports them.
        string[] files = (selected == null ? Array.Empty<string>() : [selected]).Concat(ResourceFiles(worldPath, resolver).Where(p => !missionFiles.Contains(Path.GetFullPath(p)) && !catalog.Unreadable.Contains(Path.GetFullPath(p)))).ToArray();
        return new(selected, files, unavailable, catalog.Diagnostics, unreadable);
    }
    private static async Task<MissionSceneContext> LoadMw3Async(ZbdDocument world, AssetResolver resolver, string? requested, bool exact, CancellationToken token)
    {
        var resources = await Mw3ResourcesAsync(world.Path, resolver, requested, exact, token).ConfigureAwait(false);
        // Adopt a default or fallback only if no newer selection arrived while loading. A remembered reader that
        // merely failed to open keeps its selection, so a later refresh can recover once it is readable again.
        if (resources.Mission != null && !resources.Unreadable && !string.Equals(resources.Mission, requested, StringComparison.OrdinalIgnoreCase)) resolver.TrySelectMission(world.Path, resources.Mission, requested);
        var paths = resources.Files;
        PreviewNotes loadNotes = new(); loadNotes.AddRange(resources.Diagnostics);
        if (resources.Unavailable != null)
            loadNotes.Add($"The remembered mission reader {Path.GetFileName(resources.Unavailable)} {(resources.Unreadable ? "could not be read" : "is no longer available")}; showing {(resources.Mission == null ? "stored world geometry" : Path.GetFileName(resources.Mission))}.");
        List<ZbdDocument> archives = [];
        foreach (string path in paths)
            try { if (FormatRegistry.Probe(path).Family == FormatFamily.Archive) archives.Add(await resolver.OpenCachedAsync(path, token).ConfigureAwait(false)); }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
            { loadNotes.Add($"Mission resource {Path.GetFileName(path)}: {ex.Message}"); }
        // The dependency list freezes the mission before any archive loads await.
        var selected = archives.FirstOrDefault(a => resources.Mission != null && a.Path.Equals(resources.Mission, StringComparison.OrdinalIgnoreCase) && a.Assets.Any(IsMissionAiv));
        // Report the resolved reader even if it failed to open, so the pickers can offer another.
        string? chosen = resources.Mission;
        var aivs = selected?.Assets.Where(IsMissionAiv).ToArray() ?? [];
        if (aivs.Length > 1) throw new InvalidDataException("The mission contains multiple AIV records; choose a unique resource before previewing actors.");
        var aiv = aivs.SingleOrDefault();
        var libraries = archives.Where(d => d.Game == GameVariant.MechWarrior3 && d.Scene != null).ToArray();
        var library = libraries.Length == 1 ? libraries[0] : null;
        return await Task.Run(() =>
        {
            var original = world.Scene ?? throw new InvalidDataException("Missing MW3 world scene.");
            GameScene scene = new(); scene.Models.AddRange(original.Models); scene.Materials.AddRange(original.Materials); scene.Textures.AddRange(original.Textures);
            foreach (var n in original.Nodes)
            {
                token.ThrowIfCancellationRequested();
                scene.Nodes.Add(n with { Parents = [..n.Parents], Children = [..n.Children], Data = (JsonObject)n.Data.DeepClone(), Metadata = (JsonObject)n.Metadata.DeepClone() });
            }
            List<int> sources = original.Nodes.Select(n => n.Index).ToList(); List<MissionActor> actors = []; PreviewNotes notes = new();
            notes.Add("MW3 authored layout preview. Mission scripts, AI activation, combat and inventory are not simulated.");
            notes.AddRange(loadNotes);
            int worldRoot = scene.Nodes.FirstOrDefault(n => n.Class == "world")?.Index ?? -1;
            Dictionary<string, List<GameNode>> worldActors = new(StringComparer.Ordinal);
            foreach (var node in original.Nodes)
            {
                token.ThrowIfCancellationRequested();
                if (node.Class != "object3d") continue;
                if (!worldActors.TryGetValue(node.Name, out var nodes)) worldActors.Add(node.Name, nodes = []);
                nodes.Add(node);
            }
            HashSet<int> placedWorldActors = [];
            List<int> attachedRoots = [];
            Dictionary<int, HashSet<int>> detachedChildren = [];
            Dictionary<string, List<MechAssembly>> mechTemplates = new(StringComparer.Ordinal);
            if (library?.Scene is { } libraryScene)
                foreach (var asset in library.Assets)
                {
                    token.ThrowIfCancellationRequested();
                    if (asset.Content is not MechAssembly assembly || !asset.Name.StartsWith("mech_", StringComparison.Ordinal) || !asset.Name.EndsWith(".flt", StringComparison.Ordinal) || asset.Name.Length < 9) continue;
                    string type = asset.Name[5..^4];
                    if (!libraryScene.Nodes[assembly.RootNode].Children.Any(c => libraryScene.Nodes[c].Name == type)) continue;
                    if (!mechTemplates.TryGetValue(type, out var assemblies)) mechTemplates[type] = assemblies = [];
                    assemblies.Add(assembly);
                }
            var layout = new MissionLayoutSelection(MissionDifficulty.Medium, "aiv.zrd", "vehicle.zrd", "") { MissionArchive = chosen, UnavailableMission = resources.Unreadable ? null : resources.Unavailable };
            int modelBase = scene.Models.Count;
            if (library?.Scene is { } mechs)
            {
                int materialBase = scene.Materials.Count, textureBase = scene.Textures.Count;
                scene.Textures.AddRange(mechs.Textures);
                foreach (var m in mechs.Materials)
                {
                    var copy = (JsonObject)m.DeepClone(); if (copy.Int("texture_index", -1) >= 0) copy["texture_index"] = copy.Int("texture_index") + textureBase;
                    scene.Materials.Add(copy);
                }
                scene.Models.AddRange(mechs.Models.Select(m => m with { Index = m.Index + modelBase, Polygons = m.Polygons.Select(p => p with { MaterialIndex = p.MaterialIndex < 0 ? -1 : p.MaterialIndex + materialBase }).ToArray() }));
            }
            if (aiv?.Content is ZrdNode tree && worldRoot >= 0)
            {
                var rows = tree.Children.Count == 1 && tree.Children[0].Kind == ZrdKind.Array ? tree.Children[0].Children : tree.Children;
                if (rows.Count % 2 != 0) throw new InvalidDataException("Incomplete MW3 AIV name/value pair.");
                for (int row = 0; row < rows.Count; row += 2)
                {
                    token.ThrowIfCancellationRequested();
                    string name = rows[row].Text; var data = rows[row + 1].Children;
                    string label = name[..Math.Min(name.Length, MissionActor.MaximumNamePreviewCharacters)];
                    int firstNewNode = scene.Nodes.Count;
                    int? claimedStored = null;
                    try
                    {
                        if (rows[row].Kind != ZrdKind.String || data.Count < 3 || data[1].Children.Count != 3) throw new InvalidDataException("Missing authored position/heading.");
                        float Scalar(ZrdNode n) => n.Kind == ZrdKind.Float && float.IsFinite(BitConverter.UInt32BitsToSingle(n.Bits)) ? BitConverter.UInt32BitsToSingle(n.Bits) : throw new InvalidDataException("Invalid placement scalar.");
                        Vector3 position = new(Scalar(data[1].Children[0]), Scalar(data[1].Children[1]), Scalar(data[1].Children[2])); float heading = Scalar(data[2]);
                        if (Math.Abs(position.X) > 1e12 || Math.Abs(position.Y) > 1e12 || Math.Abs(position.Z) > 1e12)
                            throw new InvalidDataException("Placement coordinates exceed the supported ±1e12 game-unit preview range.");
                        var matches = worldActors.GetValueOrDefault(name) ?? [];
                        int root;
                        if (matches.Count == 1)
                        {
                            int stored = matches[0].Index;
                            if (placedWorldActors.Add(stored)) { root = stored; claimedStored = stored; }
                            else root = Clone(original, stored, -1, 0, new(), false);
                        }
                        else if (matches.Count > 1) throw new InvalidDataException("Ambiguous world actor identity.");
                        else
                        {
                            string type = VehicleTemplateName(name);
                            var templates = worldActors.GetValueOrDefault(type) ?? [];
                            if (templates.Count == 1) root = Clone(original, templates[0].Index, -1, 0, new(), false);
                            else
                            {
                                // Base assemblies are distinct from the lower-detail and HUD members.
                                // Resolve the full member name and verify its root, never choose the first
                                // similarly named part across those independent hierarchies.
                                var candidates = mechTemplates.GetValueOrDefault(type) ?? [];
                                if (templates.Count > 1 || candidates.Count != 1) throw new InvalidDataException("No unique stored actor or mech template. Placement remains inspectable in the AIV resource.");
                                var assembly = candidates[0];
                                root = Clone(library!.Scene!, assembly.RootNode, -1, 0, new(), true);
                            }
                        }
                        SetPose(scene, root, Matrix4x4.CreateFromQuaternion(PlacementTransform.Orientation(PlacementRotationKind.HeadingDegrees, new(0, heading, 0))) * Matrix4x4.CreateTranslation(position));
                        scene.Nodes[root].Metadata["flags"] = scene.Nodes[root].Metadata.UInt("flags") | 4;
                        scene.Nodes[root].Metadata["mission_archive"] = selected!.Path;
                        scene.Nodes[root].Metadata["mission_member"] = aiv.Index;
                        scene.Nodes[root].Metadata["mission_record"] = row / 2;
                        // Reparent the preview instance on both sides. Retaining an old
                        // parent's child/partition edge renders a second, displaced copy.
                        foreach (int parent in scene.Nodes[root].Parents.Where(p => p != worldRoot))
                        {
                            if (parent < 0 || parent >= scene.Nodes.Count) continue;
                            if (!detachedChildren.TryGetValue(parent, out var removed)) detachedChildren[parent] = removed = [];
                            removed.Add(root);
                        }
                        // Matching above uses the complete authored string; only the
                        // published preview label is shortened. Record identity is unchanged.
                        scene.Nodes[root].Metadata["name_characters"] = name.Length;
                        scene.Nodes[root].Metadata["name_truncated"] = label.Length != name.Length;
                        scene.Nodes[root] = scene.Nodes[root] with { Name = label, Parents = [worldRoot] };
                        attachedRoots.Add(root);
                        actors.Add(new(root, sources[root], label, layout.Description, CoordinateSource: new(Path.GetFullPath(selected!.Path).ToUpperInvariant(), aiv.Index, aiv.Name.ToUpperInvariant(), row / 2), PlacementPosition: position, PlacementRotation: new(0, heading, 0)) { NameCharacters = name.Length });
                    }
                    catch (InvalidDataException ex)
                    {
                        // Failed hierarchies/poses must not publish orphan meshes or
                        // consume instance indices/budget needed by valid placements.
                        scene.Nodes.RemoveRange(firstNewNode, scene.Nodes.Count - firstNewNode);
                        sources.RemoveRange(firstNewNode, sources.Count - firstNewNode);
                        if (claimedStored is int stored) placedWorldActors.Remove(stored);
                        notes.Add($"{label}{(label.Length == name.Length ? "" : $"… [name truncated; {name.Length} characters]")} (AIV record {row / 2}): {ex.Message}");
                    }
                }
            }
            else notes.Add("No mission AIV resource is available; showing stored world geometry.");
            // Publish hierarchy edges once. Rebuilding the world's growing child
            // array after every placement makes a valid large AIV quadratic.
            foreach (var (parent, removed) in detachedChildren)
            {
                token.ThrowIfCancellationRequested(); var owner = scene.Nodes[parent];
                scene.Nodes[parent] = owner with { Children = owner.Children.Where(c => !removed.Contains(c)).ToArray() };
                if (owner.Class == "world" && owner.Data["partitions"] is JsonArray partitions)
                    foreach (var cell in partitions.OfType<JsonArray>().SelectMany(r => r.OfType<JsonObject>()))
                        if (cell["node_indices"] is JsonArray indices)
                            for (int i = indices.Count - 1; i >= 0; i--)
                            {
                                token.ThrowIfCancellationRequested(); long child = JsonData.Integer(indices[i]);
                                if (child is >= 0 and <= int.MaxValue && removed.Contains((int)child)) indices.RemoveAt(i);
                            }
            }
            if (attachedRoots.Count > 0)
                scene.Nodes[worldRoot] = scene.Nodes[worldRoot] with { Children = scene.Nodes[worldRoot].Children.Concat(attachedRoots).Distinct().ToArray() };
            var context = new MissionSceneContext(scene, sources, actors, [], notes, layout, original.Nodes.Count);
            if (selected != null) context.AiNetworks = MissionAiNetworks.Read(selected.Assets.Where(a => MissionAiNetworks.IsCandidate(a.Name)).Select(a => (selected, a)), token);
            context.AiNetworks = MissionAiValves.Attach(context.AiNetworks, archives.Where(a => a == selected || !a.Assets.Any(v => v.Name.Equals("aiv.zrd", StringComparison.OrdinalIgnoreCase))), token);
            return context;

            int Clone(GameScene source, int index, int parent, int depth, HashSet<int> active, bool mech)
            {
                token.ThrowIfCancellationRequested();
                if (depth > 256 || scene.Nodes.Count >= 200_000 || index < 0 || index >= source.Nodes.Count || !active.Add(index)) throw new InvalidDataException("Invalid actor hierarchy.");
                var n = source.Nodes[index]; int id = scene.Nodes.Count;
                var metadata = (JsonObject)n.Metadata.DeepClone(); metadata["source_library"] = mech ? library!.Path : world.Path; metadata["source_node"] = index;
                scene.Nodes.Add(n with { Index = id, Parents = parent < 0 ? [] : [parent], Children = [], ModelIndex = n.ModelIndex is int m ? (mech ? modelBase : 0) + m : null,
                    Metadata = metadata, Data = (JsonObject)n.Data.DeepClone() }); sources.Add(mech ? -1 : index);
                var children = n.Children.Select(c => Clone(source, c, id, depth + 1, active, mech)).ToArray();
                scene.Nodes[id] = scene.Nodes[id] with { Children = children }; active.Remove(index); return id;
            }
        }, token).ConfigureAwait(false);
    }
}
