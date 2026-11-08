using System.Numerics;
using System.Text.Json.Nodes;
using zStudio.Core.Gltf;
using zStudio.Core.Worlds;

namespace zStudio.Core.Sources;

public sealed record SourceCreationParent(string SourceFile, int GltfNode, string SourceOccurrence);
public sealed record SourceCreationScope(string File, int? Node, string Name, int Copies, IReadOnlyList<string> Missions);
public sealed record SourceCreationPlan(SourceEditPlan Edit, SourceCreationScope Parent, IReadOnlyList<int> Nodes, string Name);

public static partial class SourceObjectEdits
{
    public static SourceCreationScope CreationScope(SourceWorkspace workspace, SourceSceneState build, SourceCreationParent? parent, CancellationToken token = default)
    {
        string file, name; int copies;
        if (parent == null)
        {
            var files = build.AuthoredHierarchy.Roots.Select(r => r.File).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (files.Length != 1) throw new InvalidDataException("World has no uniquely owned mission database glTF. Choose an existing group instead.");
            file = files[0]; name = "World"; copies = build.AuthoredHierarchy.Roots.Count(r => string.Equals(r.File, file, StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            file = SourceWorkspace.Normalize(parent.SourceFile);
            var rows = build.AuthoredHierarchy.Nodes.Where(n => n.File.Equals(file, StringComparison.OrdinalIgnoreCase) && n.SourceNode == parent.GltfNode).ToArray();
            if (rows.Length == 0 || rows.Any(n => !n.Group || build.Game != GameVariant.MechWarrior3 && n.CompiledNode != null || !n.Origin.Database))
                throw new InvalidDataException("The creation parent is no longer an editable source group. Read the World tree again.");
            if (build.Provenance.Values.Any(n => n.ModelFile?.Equals(file, StringComparison.OrdinalIgnoreCase) == true && n.ModelNode == parent.GltfNode && (build.Game != GameVariant.MechWarrior3 || !n.Database)))
                throw new InvalidDataException($"{build.Mission}: the source group also survives in a live model copy. Choose a database-only group or split that shared source before adding children.");
            name = rows[0].Name; copies = rows.Length;
        }
        if (!file.EndsWith(".gltf", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"{file} must be a separate .gltf file to add children.");
        var missions = SourceBlender.MissionsUsingModel(workspace, file, token);
        if (!missions.Contains(build.Mission, StringComparer.OrdinalIgnoreCase)) throw new InvalidDataException("The map no longer loads the selected group's file; reload it.");
        return new(file, parent?.GltfNode, name, copies, missions);
    }

    public static string? CreationNameProblem(GameZWorld world, SourceSceneState build, string name)
    {
        string? problem = CopyNameProblem(world, name);
        return problem ?? (build.AuthoredHierarchy.Nodes.Any(n => n.Name == name) ? "The source hierarchy already has that name; choose another name." : null);
    }

    /// <summary>Append real source children, preserving every existing index and remapping external zone assignments.</summary>
    public static SourceCreationPlan PlanCreate(SourceWorkspace workspace, GameZWorld world, SourceSceneState build, SourceCreationScope scope,
        string name, SourceModelAddition? model = null, CancellationToken token = default)
    {
        if (CreationNameProblem(world, build, name) is { } problem) throw new InvalidDataException(problem);
        JsonObject values = new();
        if (build.Game == GameVariant.MechWarrior3) values["mw3NodeWords"] = new JsonArray(0, 160, 0, 0);
        if (model == null)
        {
            values["group"] = true;
        }
        else
        {
            var assets = SourceWorlds.ValidatePlacement(workspace, build.Mission, model, token);
            if (assets.Names.Contains(name)) throw new InvalidDataException("Choose an object name absent from the selected model's nodes.");
            var source = GltfDocument.ReadHierarchy(workspace.ReadModel(model.Model, token, GltfDocument.MaximumBufferBytes, GltfDocument.MaximumJsonBytes)!, token);
            WorldGltf.WriteNodeFlags(values, WorldGltf.RootFlags(source) ?? 0x0108001C);
            string absolute = SourceProject.Resolve(workspace.Root, model.Model);
            values["ref"] = Path.GetRelativePath(Path.GetDirectoryName(SourceProject.Resolve(workspace.Root, scope.File))!, absolute).Replace('\\', '/');
        }
        JsonObject child = new() { ["name"] = name, ["extras"] = new JsonObject { [WorldGltf.Key] = values } };
        if (model?.Position is { } position) GltfNodeEdits.SetLocal(child, Matrix4x4.CreateRotationY(model.Heading * MathF.PI / 180) * Matrix4x4.CreateTranslation(position));
        WorldNodeProvenance origin = scope.Node is int index
            ? build.AuthoredHierarchy.Nodes.First(n => n.File.Equals(scope.File, StringComparison.OrdinalIgnoreCase) && n.SourceNode == index).Origin
            : new() { ModelFile = scope.File, Database = true };
        List<int> added = []; HashSet<int> newNodes = [];
        var zones = new InstanceZoneLookup(ToProvenance(world, build, token), scope.File, token);
        var edit = GltfFile(workspace, origin, $"Add {name} inside {scope.Name}", (root, parents, _) =>
        {
            var nodes = (JsonArray)root["nodes"]!;
            if (nodes.Count + Math.Max(1, parents.Count) > GltfDocument.MaximumNodes) throw new InvalidDataException("The source file has no room for another node.");
            IEnumerable<JsonArray> lists;
            if (scope.Node == null)
            {
                if (root["scenes"] is not JsonArray scenes || scenes.Count == 0) root["scenes"] = scenes = new JsonArray(new JsonObject());
                int scene = root["scene"] == null ? 0 : GltfInteger.Int32(root["scene"]);
                var active = (JsonObject)scenes[scene]!;
                if (active["nodes"] is not JsonArray roots) active["nodes"] = roots = new();
                lists = [roots];
            }
            else lists = parents.Select(p =>
            {
                var holder = (JsonObject)nodes[p]!;
                if (holder["children"] is not JsonArray children) holder["children"] = children = new();
                return children;
            }).ToArray();
            foreach (var children in lists)
            {
                token.ThrowIfCancellationRequested();
                int created = nodes.Count; nodes.Add(child.DeepClone()); children.Add(created); added.Add(created); newNodes.Add(created);
            }
        }, token, [$"Inside {scope.Name}; source {scope.File}. Shared-source changes apply to every copy ({scope.Copies} in this map) and maps {string.Join(", ", scope.Missions)}."],
            loadZone: zones.Find, nodeZone: zones.FindNode, sceneRoots: scope.Node == null, newSourceNodes: newNodes);
        return new(edit, scope, added, name);
    }

    private static IReadOnlyDictionary<WorldNode, WorldNodeProvenance> ToProvenance(GameZWorld world, SourceSceneState build, CancellationToken token)
    {
        var slots = GameZWriter.NodeSlots(world, token);
        return slots.Where(p => build.Provenance.ContainsKey(p.Value)).ToDictionary(p => p.Key, p => build.Provenance[p.Value]);
    }

    public static IReadOnlyList<int> CreatedSlots(SourceCreationPlan plan, SourceSceneSnapshot built)
    {
        var created = plan.Nodes.ToHashSet();
        var slots = built.Provenance.Where(p => p.Value.ModelFile?.Equals(plan.Parent.File, StringComparison.OrdinalIgnoreCase) == true && created.Contains(p.Value.ModelNode))
            .Select(p => p.Key).ToArray();
        if (slots.Length == 0) throw new InvalidDataException($"The build did not place {plan.Name} inside {plan.Parent.Name}.");
        return slots;
    }

    /// <summary>Verify affected maps through the same evaluator, including texture identity and existing script targets.</summary>
    public static IReadOnlyDictionary<string, string> ValidateCreation(SourceWorkspace workspace, IReadOnlyDictionary<string, byte[]> before, SourceCreationPlan plan,
        SourceSceneSnapshot active, SourceModelAddition? model, CancellationToken token = default)
    {
        Dictionary<string, string> hashes = new(StringComparer.OrdinalIgnoreCase);
        foreach (string mission in plan.Parent.Missions)
        {
            token.ThrowIfCancellationRequested();
            var built = mission.Equals(active.Mission, StringComparison.OrdinalIgnoreCase) ? active
                : SourceWorlds.LoadSceneAsync(workspace.Root, mission, workspace.Overlay(), token).GetAwaiter().GetResult();
            var old = SourceWorlds.LoadSceneAsync(workspace.Root, mission, before, token, sourceMission: built.SourceMission).GetAwaiter().GetResult();
            var assets = model == null ? null : SourceWorlds.ValidatePlacement(workspace, mission, model, token);
            if (model == null && built.Game != GameVariant.MechWarrior3 && built.Provenance.Values.Any(n => n.ModelFile?.Equals(plan.Parent.File, StringComparison.OrdinalIgnoreCase) == true && plan.Nodes.Contains(n.ModelNode)))
                throw new InvalidDataException($"{mission}: the new group would survive in a live model copy. Choose a database-only group or split that shared source before adding children.");
            foreach (var (name, texture) in old.Textures)
            {
                token.ThrowIfCancellationRequested();
                if (!built.Textures.TryGetValue(name, out var current) || !texture.Path.Equals(current.Path, StringComparison.OrdinalIgnoreCase) || texture.Hash != current.Hash)
                    throw new InvalidDataException($"{mission}: the addition would replace existing texture {JsonData.ShownText(name, 64)} from {JsonData.ShownText(texture.Path, 192)}. Rename the new texture and update its glTF image URI, or use the existing PNG.");
            }
            if (CreationNameProblem(old.World, old.BuildState, plan.Name) is { } nameProblem)
                throw new InvalidDataException($"{mission}: {nameProblem} Choose a name unused in every map that loads {plan.Parent.File}.");
            var lookups = built.Lookups.GroupBy(l => l.Key).ToDictionary(g => g.Key, g => g.First());
            foreach (var lookup in old.Lookups.Where(l => l.Slot >= 0))
            {
                token.ThrowIfCancellationRequested();
                if (!lookups.TryGetValue(lookup.Key, out var current)
                    || !old.Provenance.TryGetValue(lookup.Slot, out var from)
                    || !built.Provenance.TryGetValue(current.Slot, out var to) || !SameSource(from, to))
                    throw new InvalidDataException($"{mission}: the addition would change {WorldLookups.Describe(lookup)} to another object. Rename the conflicting nodes in the selected model or update the definitions before adding it.");
            }
            ValidateAnimationRecipients(old, built, token);
            if (model != null || built.Game == GameVariant.MechWarrior3)
            {
                if (model != null) SourceWorlds.ValidatePlacementTextures(assets!, built, token);
                var roots = CreatedSlots(plan, built);
                if (model != null && built.Game == GameVariant.MechWarrior3)
                {
                    var slots = GameZWriter.NodeSlots(built.World, token);
                    var worlds = built.World.Nodes.Where(n => n.Class == WorldNodeClass.World).ToArray();
                    var worldRoots = worlds.Concat(worlds.SelectMany(n => n.Areas).SelectMany(a => a.Nodes)).Select(n => slots[n]).Distinct().ToArray();
                    var registered = CreatedSubtree(built, worldRoots, token);
                    if (roots.Any(root => !registered.Contains(root)))
                        throw new InvalidDataException($"{mission}: the chosen group is excluded from the rendered world. Choose World or a registered group before placing an object.");
                }
                if (model != null && roots.Any(root => built.Scene!.Nodes[root].Parents.Length == 0 || built.Scene.Nodes[root].Parents.Any(parent => built.Scene.Nodes[parent].Class != "world" && (built.Game != GameVariant.MechWarrior3 || !built.Provenance.TryGetValue(parent, out var origin) || !origin.Database || !string.Equals(origin.ModelFile, plan.Parent.File, StringComparison.OrdinalIgnoreCase)))))
                    throw new InvalidDataException($"{mission}: the addition would be inside a live model copy. Choose a database-only group or split that shared source before placing an object.");
                ValidateInitialization(workspace, before, built, old, roots, plan.Name, token);
            }
            if (old.Animations != null && built.Animations == null)
                throw new InvalidDataException($"{mission}: the addition would make its animation definitions unavailable. {built.AnimationDefinitions.Rejection}");
            if (TargetChange(old.BuildState, built.BuildState, ScriptLineChanges.None) is { } change) throw new InvalidDataException($"{mission}: {change}");
            foreach (var (file, hash) in built.ReadHashes)
                if (workspace.Read(file, token) is not { } bytes || SourceProject.Sha256(bytes) != hash)
                    throw new SourceFileChangedException($"{file} changed during validation of {mission}.", [file]);
                else hashes[file] = hash;
        }
        return hashes;
    }
}
