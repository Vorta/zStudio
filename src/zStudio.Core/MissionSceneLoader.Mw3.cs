using System.Numerics;
using System.Text.Json.Nodes;
using Recoil.Zbd.Core.Formats;

namespace Recoil.Zbd.Core;

public sealed record MissionVariant(string Archive, string Label);

public static partial class MissionSceneLoader
{
    public static async Task<IReadOnlyList<MissionVariant>> Mw3MissionsAsync(string worldPath, AssetResolver resolver, CancellationToken token = default)
    {
        List<MissionVariant> result = [];
        foreach (string path in Directory.EnumerateFiles(Path.GetDirectoryName(worldPath)!, "*.zbd").Order(StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            if (FormatRegistry.Probe(path).Family != FormatFamily.Archive) continue;
            var doc = await resolver.OpenCachedAsync(path, token).ConfigureAwait(false);
            if (doc.Assets.Any(a => a.Kind == AssetKind.Zrd && a.Name.Equals("aiv.zrd", StringComparison.OrdinalIgnoreCase)))
            {
                string name = Path.GetFileNameWithoutExtension(path);
                string label = name.StartsWith("readermp", StringComparison.OrdinalIgnoreCase) ? "Multiplayer " + name[8..] :
                    name.StartsWith("readeria", StringComparison.OrdinalIgnoreCase) ? "Instant action " + name[8..] :
                    name.StartsWith("readerm", StringComparison.OrdinalIgnoreCase) ? "Campaign " + name[7..] : name;
                result.Add(new(path, label));
            }
        }
        return result.OrderBy(m => m.Label.StartsWith("Campaign", StringComparison.Ordinal) ? 0 : 1).ThenBy(m => m.Archive, StringComparer.OrdinalIgnoreCase).ToArray();
    }
    internal static async Task<string[]> Mw3ResourceFilesAsync(string worldPath, AssetResolver resolver, CancellationToken token, string? missionArchive = null)
    {
        var choices = await Mw3MissionsAsync(worldPath, resolver, token).ConfigureAwait(false);
        string? selected = missionArchive ?? resolver.SelectedMission(worldPath);
        if (selected != null && !choices.Any(c => c.Archive.Equals(selected, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("The selected mission reader is no longer available. Choose another mission.");
        selected ??= choices.FirstOrDefault()?.Archive;
        if (selected != null && missionArchive == null) resolver.SelectMission(worldPath, selected);
        var missionFiles = choices.Select(c => c.Archive).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Only the chosen mission contributes its resources; shared map/root archives follow it.
        return (selected == null ? Array.Empty<string>() : [selected]).Concat(ResourceFiles(worldPath, resolver).Where(p => !missionFiles.Contains(p))).ToArray();
    }
    private static async Task<MissionSceneContext> LoadMw3Async(ZbdDocument world, AssetResolver resolver, CancellationToken token)
    {
        var paths = await Mw3ResourceFilesAsync(world.Path, resolver, token).ConfigureAwait(false);
        List<ZbdDocument> archives = [];
        foreach (string path in paths)
            if (FormatRegistry.Probe(path).Family == FormatFamily.Archive) archives.Add(await resolver.OpenCachedAsync(path, token).ConfigureAwait(false));
        // The dependency list freezes the mission before any archive loads await.
        var selected = archives.FirstOrDefault(a => a.Path.Equals(paths.FirstOrDefault(), StringComparison.OrdinalIgnoreCase) && a.Assets.Any(v => v.Name.Equals("aiv.zrd", StringComparison.OrdinalIgnoreCase)));
        string? chosen = selected?.Path;
        var aivs = selected?.Assets.Where(a => a.Name.Equals("aiv.zrd", StringComparison.OrdinalIgnoreCase)).ToArray() ?? [];
        if (aivs.Length > 1) throw new InvalidDataException("The mission contains multiple AIV records; choose a unique resource before previewing actors.");
        var aiv = aivs.SingleOrDefault();
        var libraries = archives.Where(d => d.Game == GameVariant.MechWarrior3 && d.Scene != null).ToArray();
        var library = libraries.Length == 1 ? libraries[0] : null;
        return await Task.Run(() =>
        {
            var original = world.Scene ?? throw new InvalidDataException("Missing MW3 world scene.");
            GameScene scene = new(); scene.Models.AddRange(original.Models); scene.Materials.AddRange(original.Materials); scene.Textures.AddRange(original.Textures);
            scene.Nodes.AddRange(original.Nodes.Select(n => n with { Parents = [..n.Parents], Children = [..n.Children], Data = (JsonObject)n.Data.DeepClone(), Metadata = (JsonObject)n.Metadata.DeepClone() }));
            List<int> sources = original.Nodes.Select(n => n.Index).ToList(); List<MissionActor> actors = []; List<string> notes = ["MW3 authored layout preview. Mission scripts, AI activation, combat and inventory are not simulated."];
            int worldRoot = scene.Nodes.FirstOrDefault(n => n.Class == "world")?.Index ?? -1;
            HashSet<int> placedWorldActors = [];
            var layout = new MissionLayoutSelection(MissionDifficulty.Medium, "aiv.zrd", "vehicle.zrd", "") { MissionArchive = chosen };
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
                    try
                    {
                        if (rows[row].Kind != ZrdKind.String || data.Count < 3 || data[1].Children.Count != 3) throw new InvalidDataException("Missing authored position/heading.");
                        float Scalar(ZrdNode n) => n.Kind == ZrdKind.Float && float.IsFinite(BitConverter.UInt32BitsToSingle(n.Bits)) ? BitConverter.UInt32BitsToSingle(n.Bits) : throw new InvalidDataException("Invalid placement scalar.");
                        Vector3 position = new(Scalar(data[1].Children[0]), Scalar(data[1].Children[1]), Scalar(data[1].Children[2])); float heading = Scalar(data[2]);
                        var matches = original.Nodes.Where(n => n.Class == "object3d" && n.Name == name).ToArray();
                        int root;
                        if (matches.Length == 1)
                        {
                            int stored = matches[0].Index;
                            root = placedWorldActors.Add(stored) ? stored : Clone(original, stored, -1, 0, new(), false);
                        }
                        else if (matches.Length > 1) throw new InvalidDataException("Ambiguous world actor identity.");
                        else
                        {
                            string type = VehicleTemplateName(name);
                            var templates = original.Nodes.Where(n => n.Class == "object3d" && n.Name == type).ToArray();
                            if (templates.Length == 1) root = Clone(original, templates[0].Index, -1, 0, new(), false);
                            else
                            {
                                // Base assemblies are distinct from the lower-detail and HUD members.
                                // Resolve the full member name and verify its root, never choose the first
                                // similarly named part across those independent hierarchies.
                                var candidates = library?.Assets.Where(a => a.Name.Equals("mech_" + type + ".flt", StringComparison.Ordinal) && a.Content is MechAssembly ma && library.Scene!.Nodes[ma.RootNode].Children.Any(c => library.Scene.Nodes[c].Name == type)).ToArray() ?? [];
                                if (templates.Length > 1 || candidates.Length != 1) throw new InvalidDataException("No unique stored actor or mech template. Placement remains inspectable in the AIV resource.");
                                var assembly = (MechAssembly)candidates[0].Content!;
                                root = Clone(library!.Scene!, assembly.RootNode, -1, 0, new(), true);
                            }
                        }
                        SetPose(scene, root, Matrix4x4.CreateRotationY(heading * MathF.PI / 180) * Matrix4x4.CreateTranslation(position));
                        scene.Nodes[root].Metadata["flags"] = scene.Nodes[root].Metadata.UInt("flags") | 4;
                        scene.Nodes[root].Metadata["mission_archive"] = selected!.Path;
                        scene.Nodes[root].Metadata["mission_member"] = aiv.Index;
                        scene.Nodes[root].Metadata["mission_record"] = row / 2;
                        // Reparent the preview instance on both sides. Retaining an old
                        // parent's child/partition edge renders a second, displaced copy.
                        foreach (int parent in scene.Nodes[root].Parents.Where(p => p != worldRoot))
                        {
                            if (parent < 0 || parent >= scene.Nodes.Count) continue;
                            var owner = scene.Nodes[parent];
                            scene.Nodes[parent] = owner with { Children = owner.Children.Where(c => c != root).ToArray() };
                            if (owner.Class == "world" && owner.Data["partitions"] is JsonArray partitions)
                                foreach (var cell in partitions.OfType<JsonArray>().SelectMany(r => r.OfType<JsonObject>()))
                                    if (cell["node_indices"] is JsonArray indices)
                                        for (int i = indices.Count - 1; i >= 0; i--) if (JsonData.Integer(indices[i]) == root) indices.RemoveAt(i);
                        }
                        scene.Nodes[root] = scene.Nodes[root] with { Name = name, Parents = [worldRoot] };
                        scene.Nodes[worldRoot] = scene.Nodes[worldRoot] with { Children = scene.Nodes[worldRoot].Children.Append(root).Distinct().ToArray() };
                        actors.Add(new(root, sources[root], name, layout.Description, CoordinateSource: new(Path.GetFullPath(selected!.Path).ToUpperInvariant(), aiv.Index, aiv.Name.ToUpperInvariant(), row / 2), PlacementPosition: position, PlacementRotation: new(0, heading, 0)));
                    }
                    catch (InvalidDataException ex) { notes.Add($"{name} (AIV record {row / 2}): {ex.Message}"); }
                }
            }
            else notes.Add("No mission AIV resource is available; showing stored world geometry.");
            var context = new MissionSceneContext(scene, sources, actors, [], notes, layout, original.Nodes.Count);
            if (selected != null) context.AiNetworks = MissionAiNetworks.Read(selected.Assets.Where(a => MissionAiNetworks.IsCandidate(a.Name)).Select(a => (selected, a)), token);
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
