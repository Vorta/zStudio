using System.Text.Json.Nodes;
using zStudio.Core.Worlds;

namespace zStudio.Core.Sources;

public static partial class SourceObjectEdits
{
    /// <summary>
    /// A plan that renames an object of the mission database in its glTF file (every copy of an instance or part holding it).
    /// Scripts, animations and resources find objects by name: an instruction that finds the object by its old name refuses
    /// the rename; animations and resources that name it (such as ai.zrd turret patterns) must be changed with it.
    /// </summary>
    public static SourceEditPlan PlanRename(SourceObjectTarget target, string name, CancellationToken token = default)
    {
        var node = target.Node; var origin = target.Origin;
        Generated(origin, node.Name);
        CheckName(name);
        if (node.Class is not (WorldNodeClass.Object3D or WorldNodeClass.Lod)) throw new InvalidDataException($"{node.Name} is a {node.Class} node; only objects can be renamed here.");
        if (name == node.Name) throw new InvalidDataException($"{node.Name} already has that name.");
        if (target.World.Nodes.Any(n => n.Name == name)) throw new InvalidDataException($"The world already has a node named {name}; choose another name.");
        RequireDatabaseNode(node, origin, "renamed");
        RefuseUsers(target, [node], node.Name, "renamed");
        RefuseAnimationRoots(target, node.Name, name, token);
        List<string> notes = [$"Animations and resources that find {node.Name} by name no longer find it, and those that name {name} now find this object; Problems lists what the rebuild reports."];
        return GltfFile(target.Workspace, origin, $"Rename {node.Name} to {name}", (root, copies, _) =>
        {
            foreach (int copy in copies)
            {
                token.ThrowIfCancellationRequested();
                var renamed = (JsonObject)root["nodes"]![copy]!;
                renamed["name"] = name;
                // The engine name too: an editor's suffix rules must not change it on import.
                Recoil(renamed)["name"] = name;
            }
        }, token, notes) with { Lines = ScriptLineChanges.Renamed(origin.ModelFile!, origin.ModelNodeName ?? node.Name, name, node.Name) };
    }

    /// <summary>
    /// A plan that renames a node of the mission database the build removes (a group, or a reference holder whose objects it
    /// keeps), so the game never uses its name: the editor's tree shows it, and a node slot the build left free keeps it as
    /// inert bytes (shipped worlds reuse those slots, except m6). The node is <paramref name="file"/>'s glTF node
    /// <paramref name="gltfNode"/>, named <paramref name="expected"/> when given; every copy of an instance or part holding it is
    /// renamed, so every copy must be removed. A script instruction that acts on a copy refuses the rename, and so does a
    /// name another node of the world or of the sources has; the rebuild must also leave every instruction on its nodes.
    /// </summary>
    public static SourceEditPlan PlanRenameRemoved(SourceWorkspace workspace, GameZWorld world, SourceSceneState build, string file, int gltfNode, string? expected, string name, CancellationToken token = default)
    {
        file = file.Replace('\\', '/');
        bool Same(string? path, int index) => index == gltfNode && string.Equals(path, file, StringComparison.OrdinalIgnoreCase);
        var copies = build.AuthoredHierarchy.Nodes.Where(n => Same(n.File, n.SourceNode)).ToList();
        if (copies.Count == 0) throw new InvalidDataException($"Node {gltfNode} of {file} is not a node of the shown world's sources.");
        var node = copies[0]; var origin = node.Origin;
        if (expected != null && node.Name != expected) throw new InvalidDataException($"Node {gltfNode} of {file} is {node.Name}, not {expected}; select it again.");
        // The file's node is renamed for every copy: none may be in the built world, where the game finds nodes by name.
        if (copies.Any(n => n.CompiledNode != null) || build.Provenance.Values.Any(p => Same(p.ModelFile, p.ModelNode)))
            throw new InvalidDataException($"{node.Name} (or a copy of it) is a node of the built world; rename it as an object.");
        Generated(origin, node.Name);
        CheckName(name);
        if (name == node.Name) throw new InvalidDataException($"{node.Name} already has that name.");
        if (origin.ModelFile == null || !origin.Database || origin.ModelNode < 0)
            throw new InvalidDataException($"{node.Name} is not a node of the mission database's glTF files; only those can be renamed here.");
        if (copies.SelectMany(n => n.Origin.Applied.Concat(n.Origin.Named)).FirstOrDefault() is { } user)
            throw new InvalidDataException($"{user.Script} line {user.Line} ({user.Command}) acts on {node.Name}; it cannot be renamed until that instruction changes.");
        if (world.Nodes.Any(n => n.Name == name) || build.AuthoredHierarchy.Nodes.Any(n => n.Name == name))
            throw new InvalidDataException($"The world or its sources already have a node named {name}; choose another name.");
        List<string> notes = [$"The build removes {node.Name}, so the game never uses its name; a node slot the build left free keeps {name} as inert bytes."];
        return GltfFile(workspace, origin, $"Rename {node.Name} to {name}", (root, copies, _) =>
        {
            foreach (int copy in copies)
            {
                token.ThrowIfCancellationRequested();
                var renamed = (JsonObject)root["nodes"]![copy]!;
                renamed["name"] = name;
                // An engine name in the extras would win on import: keep it with the shown name.
                if ((renamed["extras"] as JsonObject)?[WorldGltf.Key] is JsonObject recoil && recoil.ContainsKey("name")) recoil["name"] = name;
            }
        }, token, notes) with { Lines = ScriptLineChanges.Renamed(origin.ModelFile, node.Name, name) };
    }

    /// <summary>
    /// A plan that makes an object of the mission database reference another model: the external reference the object holds
    /// (its own, or that of its one referencing child, as OpenFlight placements hold a model) names <paramref name="model"/>
    /// instead, keeping the object's name, place, flags and zone. Zone overrides the placement held for the previous model's
    /// nodes are removed, so the new model's nodes take the model's own zones (inheriting the object's).
    /// </summary>
    public static SourceEditPlan PlanReplaceModel(SourceObjectTarget target, string model, CancellationToken token = default)
    {
        var node = target.Node; var origin = target.Origin;
        Generated(origin, node.Name);
        if (node.Class is not (WorldNodeClass.Object3D or WorldNodeClass.Lod)) throw new InvalidDataException($"{node.Name} is a {node.Class} node; only objects reference models.");
        RequireDatabaseNode(node, origin, "given another model");
        model = model.Replace('\\', '/');
        if (!model.EndsWith(".gltf", StringComparison.OrdinalIgnoreCase) || !model.StartsWith(SourceProject.DataFolder + "/", StringComparison.OrdinalIgnoreCase) || model.Split('/').Any(part => part is "" or "." or ".."))
            throw new InvalidDataException("Name a .gltf model of the project's data folder by its project path, such as data/common/models/sonictur.gltf.");
        // Outer placements key nested zone overrides by this file's reference nodes; they would describe the previous model.
        if (origin.Part || !string.Equals(origin.ZoneOwnerPath ?? origin.ModelFile, origin.ModelFile, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"{node.Name} is in {origin.ModelFile}, which {origin.ZoneOwnerPath} references with its own zone assignments; change the model in Blender, where those placements are kept.");
        if (!target.Workspace.Exists(model, token)) throw new InvalidDataException($"The project has no model {model}.");
        string file = origin.ModelFile!;
        if (string.Equals(model, file, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"{file} cannot reference itself.");
        string uri = Path.GetRelativePath(Path.GetDirectoryName(file)!, model).Replace('\\', '/');
        // The previous model's nodes (and a holder that is renamed with it) leave or change: a script instruction acting on one
        // would act on another node, or none. The object's other children stay.
        var holderNode = BuiltHolder(target, node) ?? node;
        RefuseUsers(target, Subtree(holderNode).Where(n => !ReferenceEquals(n, node)), node.Name, "given another model");
        string label = $"Make {node.Name} reference {Path.GetFileName(model)}";
        List<string> notes = [$"Animations and resources that find a part of {node.Name} by name find the new model's parts; Problems lists what the rebuild reports."];
        string? previous = null; bool overridesRemoved = false;
        return GltfFile(target.Workspace, origin, label, (root, copies, _) =>
        {
            var nodes = (JsonArray)root["nodes"]!;
            foreach (int copy in copies)
            {
                token.ThrowIfCancellationRequested();
                var holder = ReferenceHolder(nodes, copy, node.Name);
                var recoil = Recoil(holder);
                if (recoil.ContainsKey(WorldGltf.ZoneReference))
                    throw new InvalidDataException($"{node.Name} references its model through a map zone binding; change the reference in Blender.");
                string old = recoil["ref"]!.GetValue<string>();
                if (string.Equals(old, uri, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"{node.Name} already references {model}.");
                previous ??= old;
                recoil["ref"] = uri;
                overridesRemoved |= recoil.Remove("zoneOverrides");
                // An OpenFlight holder is named after the file it loads (pturret.flt): keep that naming for the new model.
                string flt = Path.GetFileNameWithoutExtension(old) + ".flt";
                if (holder["name"] is JsonValue shown && shown.TryGetValue(out string? holderName) && string.Equals(holderName, flt, StringComparison.OrdinalIgnoreCase)
                    && !ReferenceEquals(holder, nodes[copy]))
                {
                    string renamed = Path.GetFileNameWithoutExtension(model) + ".flt";
                    holder["name"] = renamed;
                    if (recoil.ContainsKey("name")) recoil["name"] = renamed;
                }
            }
            // The plan copies the notes after this change, so these reach it.
            if (previous != null) notes.Insert(0, $"{node.Name} referenced {previous}.");
            if (overridesRemoved) notes.Add($"The placement's zone overrides for the previous model were removed: the new model's nodes take the model's own zones, inheriting {node.Name}'s.");
        }, token, notes, refuseWhenBound: $"A map file references {file} with zone assignments for its references; change {node.Name}'s model in Blender, where those placements are kept.");
    }

    /// <summary>The built node holding the object's reference: the object, or its child whose loaded roots record it as their holder.</summary>
    private static WorldNode? BuiltHolder(SourceObjectTarget target, WorldNode node)
    {
        foreach (var holder in node.Children.Prepend(node))
            if (target.Provenance.TryGetValue(holder, out var held))
                foreach (var root in holder.Children)
                    if (target.Provenance.TryGetValue(root, out var loaded) && ReferenceEquals(loaded.ReferencedBy, held)) return holder;
        return null;
    }

    /// <summary>The node holding an object's external reference: the object itself, or its one child that holds one.</summary>
    private static JsonObject ReferenceHolder(JsonArray nodes, int index, string name)
    {
        var node = (JsonObject)nodes[index]!;
        if (HasReference(node)) return node;
        List<JsonObject> holders = [];
        if (node["children"] is JsonArray children)
            foreach (var child in children)
                if (child is JsonValue value && value.TryGetValue(out int at) && at >= 0 && at < nodes.Count && nodes[at] is JsonObject candidate && HasReference(candidate))
                    holders.Add(candidate);
        return holders.Count switch
        {
            1 => holders[0],
            0 => throw new InvalidDataException($"{name} references no model file: its geometry is in the mission database itself. Add the model as a new object instead."),
            _ => throw new InvalidDataException($"{name} references several model files; change the reference of the one you mean."),
        };
        static bool HasReference(JsonObject candidate) => (candidate["extras"] as JsonObject)?[WorldGltf.Key] is JsonObject recoil && (recoil["ref"] != null || recoil[WorldGltf.ZoneReference] != null);
    }

    /// <summary>
    /// Refuses a rename that would move or drop an animation of the mission bound to the node's name. A NAME list binds to the
    /// first listed name the world has (the shipped destroy_turret lists tur_101 and binds to it in m1, and turrets elsewhere
    /// use it through that binding), so renaming the bound node would take the animation out of the mission.
    /// </summary>
    private static void RefuseAnimationRoots(SourceObjectTarget target, string old, string name, CancellationToken token)
    {
        var names = target.World.Nodes.Select(n => n.Name).ToHashSet(StringComparer.Ordinal);
        var after = new HashSet<string>(names, StringComparer.Ordinal) { name };
        // Another node keeps the name: what binds by it still finds a node, but the new name may complete a pattern.
        if (target.World.Nodes.Count(n => n.Name == old) > 1) { RefuseAnimationNames(); return; }
        after.Remove(old);
        IReadOnlyList<(string File, string Animation, IReadOnlyList<string> Roots)> bound;
        try { bound = SourceWorlds.AnimationsBoundTo(target.Workspace.Root, target.Mission, old, names, after, target.Workspace.Overlay(), token); }
        catch (IOException ex) { throw new InvalidDataException($"The mission's animation definitions could not be checked for {old}: {ex.Message}", ex); }
        var lost = bound.Where(b => !b.Roots.Contains(name)).ToList();
        if (lost.Count == 0) { RefuseAnimationNames(); return; }
        throw new InvalidDataException($"{string.Join("; ", lost.Take(4).Select(b => $"{b.Animation} ({b.File}) is bound to {old}" + (b.Roots.Count == 0 ? ", and no other name it lists is in this world" : $" and would bind to {b.Roots[0]} instead")))}"
            + (lost.Count > 4 ? $"; and {lost.Count - 4} more" : "")
            + $". Renamed, the mission would {(lost.All(b => b.Roots.Count == 0) ? "lose" : "move")} the animation (a turret's destroy animation also resets the destroyed parts of every turret that uses it). Add {name} to its NAME list, in a definition this mission lists, before renaming.");

        // A definition can also name the node elsewhere (the node it attaches to, the nodes its events act on, through
        // patterns too), or the new name can complete one: the mission's animations are compiled with the renamed world,
        // and a rejection the current world does not have refuses the rename.
        void RefuseAnimationNames()
        {
            var files = new SourceVtolDropEdits.WorkspaceFiles(target.Workspace, token);
            string root = SourceBuilder.AnimationRoot(target.Mission);
            string? rejection;
            try
            {
                if (!files.Exists(root) || Animation.AnimationCompiler.Evaluate(files, root, after.ToArray(), null, token).EngineRejection is not { } renamed) return;
                if (Animation.AnimationCompiler.Evaluate(files, root, names.ToArray(), null, token).EngineRejection != null) return;
                rejection = renamed;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException) { throw new InvalidDataException($"The mission's animation definitions could not be checked for {old}: {ex.Message}", ex); }
            throw new InvalidDataException($"Renamed, the game would reject the mission's animation file: {JsonData.ShownText(rejection, 512)} Change those definitions first, or choose another name.");
        }
    }

    private static JsonObject Recoil(JsonObject node)
    {
        if (node["extras"] is not JsonObject extras) node["extras"] = extras = new();
        if (extras[WorldGltf.Key] is not JsonObject recoil) extras[WorldGltf.Key] = recoil = new();
        return recoil;
    }

    private static void RequireDatabaseNode(WorldNode node, WorldNodeProvenance origin, string verb)
    {
        if (origin.ModelFile == null) throw new InvalidDataException($"{node.Name} was made by a script; it can be {verb} only in the script itself.");
        if (!origin.Database) throw new InvalidDataException($"{node.Name} is part of the model file {origin.ModelFile}; only objects of the mission database can be {verb} here.");
    }
}
