using System.IO;
using System.Numerics;
using System.Text.Json.Nodes;
using Recoil.Zbd.Automation;
using Recoil.Zbd.Core.Formats;
using Recoil.Zbd.Core.Sources;
using Recoil.Zbd.Core.Worlds;

namespace Recoil.Zbd.Desktop;

/// <summary>
/// World objects of source worlds: each node of the built world knows the source that made it (see
/// <see cref="WorldNodeProvenance"/>), and moving it or changing its flags edits that source — the mission database's glTF
/// node, the model file's node or the script instruction that placed it — as one change of the project's workspace.
/// </summary>
public partial class MainWindow
{
    /// <summary>The built world read once per document, for current transforms and flags.</summary>
    private sealed record SourceWorldModelEntry(GameZWorld World, IReadOnlyDictionary<int, WorldNode> Slots)
    {
        public IReadOnlyDictionary<WorldNode, WorldNodeProvenance>? Provenance { get; set; }
    }
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<DocumentModel, SourceWorldModelEntry> sourceWorldModels = new();
    private SourceWorldModelEntry SourceWorldModel(DocumentModel doc)
    {
        if (sourceWorldModels.TryGetValue(doc, out var cached)) return cached;
        var world = GameZWorldReader.FromDocument(doc.Document, doc.Lifetime.Token);
        SourceWorldModelEntry model = new(world, GameZWriter.NodeSlots(world).ToDictionary(p => p.Value, p => p.Key));
        sourceWorldModels.AddOrUpdate(doc, model);
        return model;
    }

    /// <summary>A resource editor may not change a project file the source workspace holds unsaved edits of; the edits would conflict on save.</summary>
    private void RefuseResourceEditOfWorkspaceFile(DocumentModel doc)
    {
        if (sourceWorkspace is not { } workspace) return;
        string full = Path.GetFullPath(doc.Path), prefix = workspace.Root + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return;
        string relative = SourceProject.Relative(workspace.Root, full);
        if (workspace.IsFileDirty(relative))
            throw new InvalidOperationException($"The source project holds unsaved edits of {relative}; save or undo them before editing the file here.");
    }
    /// <summary>The world-file node a shown scene node is, or null for a runtime copy (a placed vehicle or pickup instance).</summary>
    private int? SourceObjectNode(int node)
    {
        if (scene?.Mission is not { } mission) return node;
        int source = node >= 0 && node < mission.SourceNodes.Count ? mission.SourceNodes[node] : node;
        return source == node ? node : null;
    }
    /// <summary>What a node of a source world is and where it came from.</summary>
    private SourceObjectState DescribeSourceObject(DocumentModel doc, int node)
    {
        if (doc.SourceWorld == null || doc.SourceBuild is not { } build) throw new StudioCommandException("unsupported", "This document is not a source world.");
        if (!build.Provenance.TryGetValue(node, out var origin)) throw new StudioCommandException("read_only", $"Scene node {node} has no recorded source.");
        WorldNode? built;
        try { built = SourceWorldModel(doc).Slots.GetValueOrDefault(node); }
        catch (InvalidDataException ex) { throw new StudioCommandException("build_failed", ex.Message); }
        if (built == null) throw new StudioCommandException("stale_record", $"Scene node {node} is not in the built world.");
        // An object whose identity flag is set has the identity transform (LocalMatrix is null for it).
        ObjectTransform? transform = built.Class == WorldNodeClass.Object3D ? ObjectTransform.FromMatrix(WorldUpdate.LocalMatrix(built) ?? System.Numerics.Matrix4x4.Identity) : null;
        List<string> notes = [];
        string source;
        if (origin.ModelFile != null)
        {
            source = $"{origin.ModelFile} node {origin.ModelNode}" + (origin.Load is { } load ? $", loaded by {load.Script} line {load.Line}" : "");
            if (!origin.Database) notes.Add($"A node of the model file {origin.ModelFile}: transform and flag edits apply wherever that file is loaded.");
        }
        else source = origin.Created is { } created ? $"{created.Script} line {created.Line} ({created.Command})" : "the scripts";
        foreach (var (command, writer) in origin.Writers.OrderBy(w => w.Value.Script).ThenBy(w => w.Value.Line).Take(16))
        {
            int runs = doc.SourceBuild.Executions.GetValueOrDefault((writer.Script, writer.Line));
            notes.Add($"{command}: {writer.Script} line {writer.Line}" + (runs > 1 ? $" (runs {runs} times; edit the script directly)" : ""));
        }
        var whole = SourceObjectEdits.ObjectOf(built, SourceWorldProvenance(doc));
        return new(node, built.Name, built.Class.ToString(), transform, built.Flags, origin, source, notes)
        {
            Parent = built.Parents.FirstOrDefault()?.Name, Object = ReferenceEquals(whole, built) ? null : whole.Name,
        };
    }
    /// <summary>The provenance of the shown world's nodes, by node.</summary>
    private IReadOnlyDictionary<WorldNode, WorldNodeProvenance> SourceWorldProvenance(DocumentModel doc)
    {
        var model = SourceWorldModel(doc);
        if (model.Provenance is { } cached) return cached;
        Dictionary<WorldNode, WorldNodeProvenance> provenance = new(ReferenceEqualityComparer.Instance);
        if (doc.SourceBuild is { } build) foreach (var (slot, node) in model.Slots) if (build.Provenance.TryGetValue(slot, out var origin)) provenance[node] = origin;
        model.Provenance = provenance;
        return provenance;
    }
    /// <summary>The object a structural edit of a shown node applies to, with the world and provenance it is checked against.</summary>
    private SourceObjectTarget SourceObjectTargetFor(DocumentModel doc, int node, SourceWorkspace workspace)
    {
        if (doc.SourceWorld is not { } session || doc.SourceBuild is not { } build) throw new StudioCommandException("unsupported", "This document is not a source world.");
        var model = SourceWorldModel(doc);
        var built = model.Slots.GetValueOrDefault(node) ?? throw new StudioCommandException("stale_record", $"Scene node {node} is not in the built world.");
        var provenance = SourceWorldProvenance(doc);
        return new(workspace, session.Mission, model.World, SourceObjectEdits.ObjectOf(built, provenance), provenance, build.Executions) { Write = build.WriteInstruction };
    }
    /// <summary>A node of the shown world by name, refused when no node or several nodes have it.</summary>
    private WorldNode SourceWorldNodeNamed(DocumentModel doc, string name)
    {
        var matches = SourceWorldModel(doc).World.Nodes.Where(n => n.Name == name).Take(2).ToList();
        return matches.Count switch
        {
            0 => throw new StudioCommandException("invalid_argument", $"The world has no node named {name}."),
            1 => matches[0],
            _ => throw new StudioCommandException("invalid_argument", $"Several nodes are named {name}; choose the parent by its node index (zstudio_source_world_object_edit parent)."),
        };
    }
    /// <summary>Plans a delete, copy or re-parenting of the object a node belongs to and rebuilds the world with it.</summary>
    private Task<DocumentModel> EditSourceStructureAsync(DocumentModel doc, int node, Func<SourceObjectTarget, SourceEditPlan> plan, CancellationToken token)
    {
        var state = DescribeSourceObject(doc, node);
        List<SourceModelAddition> additions = [];
        return EditSourceWorldAsync(doc, $"Editing {state.Object ?? state.Name}", workspace =>
        {
            var planned = plan(SourceObjectTargetFor(doc, node, workspace));
            foreach (string note in planned.Notes) ViewModel.Status = note;
            additions.AddRange(planned.Additions);
            return workspace.Apply(planned.Label, planned.Changes.Select(c => (c.Relative, (byte[]?)c.Content)), token) is { } t ? () => workspace.Retract(t) : null;
        }, token, additions);
    }
    private Task<DocumentModel> DeleteSourceObjectAsync(DocumentModel doc, int node, CancellationToken token) =>
        EditSourceStructureAsync(doc, node, target => SourceObjectEdits.PlanDelete(target, token), token);
    private Task<DocumentModel> DuplicateSourceObjectAsync(DocumentModel doc, int node, string name, ObjectTransform? transform, CancellationToken token) =>
        EditSourceStructureAsync(doc, node, target => SourceObjectEdits.PlanDuplicate(target, name, transform, token), token);
    /// <summary>Moves the object under <paramref name="parent"/> (a scene node index; null for the world).</summary>
    private Task<DocumentModel> ReparentSourceObjectAsync(DocumentModel doc, int node, int? parent, CancellationToken token)
    {
        WorldNode? into = null;
        if (parent is int p)
        {
            into = SourceWorldModel(doc).Slots.GetValueOrDefault(p) ?? throw new StudioCommandException("stale_record", $"Scene node {p} is not in the built world.");
            if (into.Class == WorldNodeClass.World) into = null;
        }
        return EditSourceStructureAsync(doc, node, target => SourceObjectEdits.PlanReparent(target, into, token), token);
    }
    /// <summary>The scene node index of the newest node with a name in a rebuilt world, for following a copy.</summary>
    private static int? SourceNodeNamed(DocumentModel doc, string name) =>
        doc.SourceBuild is { } build && GameZWorldReader.FromDocument(doc.Document, doc.Lifetime.Token) is var world
            ? GameZWriter.NodeSlots(world).Where(p => p.Key.Name == name).Select(p => (int?)p.Value).DefaultIfEmpty(null).Max() : null;

    /// <summary>Plans one edit of a source world's object against the workspace and rebuilds the world with it.</summary>
    private Task<DocumentModel> EditSourceObjectAsync(DocumentModel doc, int node, Func<SourceWorkspace, SourceObjectState, IReadOnlyDictionary<(string Script, int Line), int>, SourceEditPlan> plan, CancellationToken token)
    {
        var state = DescribeSourceObject(doc, node);
        var executions = doc.SourceBuild!.Executions;
        string label = $"Editing {state.Name}";
        return EditSourceWorldAsync(doc, label, workspace =>
        {
            var planned = plan(workspace, state, executions);
            foreach (string note in planned.Notes) ViewModel.Status = note;
            return workspace.Apply(planned.Label, planned.Changes.Select(c => (c.Relative, (byte[]?)c.Content)), token) is { } t ? () => workspace.Retract(t) : null;
        }, token);
    }
    private Task<DocumentModel> MoveSourceObjectAsync(DocumentModel doc, int node, ObjectTransform transform, CancellationToken token) =>
        EditSourceObjectAsync(doc, node, (w, s, e) => SourceObjectEdits.PlanTransform(w, s.Name, s.Origin, e, transform, token, doc.SourceWorld?.Mission, s.Transform), token);
    private Task<DocumentModel> FlagSourceObjectAsync(DocumentModel doc, int node, uint bit, bool on, CancellationToken token) =>
        EditSourceObjectAsync(doc, node, (w, s, e) => SourceObjectEdits.PlanFlag(w, s.Name, s.Origin, e, bit, on, token, doc.SourceWorld?.Mission), token);
    private Task<DocumentModel> CommandSourceObjectAsync(DocumentModel doc, int node, string command, IReadOnlyList<string> args, CancellationToken token)
    {
        if (!SourceObjectEdits.PropertyCommands.ContainsKey(command)) throw new StudioCommandException("invalid_argument", $"{command} is not a property command (see zstudio_source_world_object).");
        if (args.Count is 0 or > 8 || args.Any(a => a.Length is 0 or > 64)) throw new StudioCommandException("invalid_argument", "Give 1–8 arguments of up to 64 characters.");
        // The interpreter applies World… commands to worlds, Light… to lights and so on; others would do nothing.
        if (!CommandFits(DescribeSourceObject(doc, node).Class, command)) throw new StudioCommandException("invalid_argument", $"{command} does not apply to a {DescribeSourceObject(doc, node).Class} node.");
        return EditSourceObjectAsync(doc, node, (w, s, e) => SourceObjectEdits.PlanCommand(w, s.Name, s.Origin, e, command, args, token, doc.SourceWorld?.Mission), token);
    }

    /// <summary>Opens Properties for a source world's object; after an edit it follows the object into the rebuilt world.</summary>
    private bool ShowSourceObjectProperties(DocumentModel doc, int node)
    {
        var state = DescribeSourceObject(doc, node);
        // A terrain piece is compiled from its recipe; Properties edits the recipe.
        if (state.Origin.Terrain is { } recipe)
            return ShowTerrainProperties(doc, recipe, state.Origin.TerrainSurface, $"Piece {state.Name}: surface {state.Origin.TerrainSurface}, cell {state.Origin.TerrainCell.Column}, {state.Origin.TerrainCell.Row}", null);
        var window = GetPropertiesWindow();
        SourceObjectPropertiesEditor fields = new(state,
            transform => FollowSourceObjectAsync(state, () => MoveSourceObjectAsync(doc, node, transform, CancellationToken.None)),
            (bit, on) => FollowSourceObjectAsync(state, () => FlagSourceObjectAsync(doc, node, bit, on, CancellationToken.None)),
            (command, args) => FollowSourceObjectAsync(state, () => CommandSourceObjectAsync(doc, node, command, args, CancellationToken.None)),
            new(parent => FollowSourceObjectAsync(state, () => ReparentSourceObjectAsync(doc, node, GameZWriter.NodeSlots(SourceWorldModel(doc).World)[SourceWorldNodeNamed(doc, parent)], CancellationToken.None)),
                async name =>
                {
                    var next = await DuplicateSourceObjectAsync(doc, node, name, null, CancellationToken.None);
                    if (!next.IsDisposed && SourceNodeNamed(next, name) is int copy && (propertiesWindow?.Document == null || propertiesWindow.Document == next))
                        try { ShowSourceObjectProperties(next, copy); } catch (StudioCommandException ex) { ViewModel.Status = ex.Message; }
                },
                async () => { await DeleteSourceObjectAsync(doc, node, CancellationToken.None); propertiesWindow?.Close(); }));
        bool opened = window.SetSourceObject(doc, fields);
        PresentProperties(window, opened);
        return opened;
    }
    /// <summary>Runs an edit, then shows the same object of the rebuilt world in Properties (found by its source).</summary>
    private async Task FollowSourceObjectAsync(SourceObjectState state, Func<Task<DocumentModel>> edit)
    {
        var next = await edit();
        if (next.SourceBuild is not { } build) return;
        int? node = build.Provenance.Where(p => new SourceObjectState(p.Key, "", "", null, 0, p.Value, "", []).Identity == state.Identity).Select(p => (int?)p.Key).FirstOrDefault();
        if (node is int found && !next.IsDisposed && (propertiesWindow == null || propertiesWindow.Document == null || propertiesWindow.Document == next))
            try { ShowSourceObjectProperties(next, found); } catch (StudioCommandException ex) { ViewModel.Status = ex.Message; }
    }

    private void RegisterSourceObjectCommands(StudioCommands r)
    {
        Register(r, "source_world_object", "Describe a world object of a source world: its name, class, local transform (position, rotation in degrees about Y then X then Z, scale), node flags, and where it came from — the glTF file and node it was imported from, or the script instruction that created it — with the instruction that last set each property and how often it ran. Node indices are those of zstudio_scene_nodes for the shown world.", false,
            [DocumentParameter, new("node", "integer", "Scene node index.", true, Minimum: 0, Maximum: int.MaxValue)], a =>
        {
            var d = TargetDocument(a); var state = DescribeSourceObject(d, Int(a, "node"));
            var origin = state.Origin;
            return Result(new
            {
                document = d.SessionId, revision = d.Revision, @object = state.Json(),
                origin = new
                {
                    modelFile = origin.ModelFile, modelNode = origin.ModelFile == null ? (int?)null : origin.ModelNode, database = origin.Database,
                    load = Instruction(origin.Load), created = Instruction(origin.Created), attached = Instruction(origin.Attached),
                    writers = origin.Writers.Take(32).ToDictionary(w => w.Key, w => Instruction(w.Value))
                },
                editableFlags = SourceObjectPropertiesEditor.EditableFlags.Select(f => new { bit = $"0x{f.Bit:X}", label = f.Label, on = (state.Flags & f.Bit) != 0 }).ToArray(),
                applied = origin.Applied.Take(32).Select(Instruction).ToArray(), appliedCount = origin.Applied.Count,
                propertyCommands = SourceObjectEdits.PropertyCommands.Select(p => new { command = p.Key, arguments = p.Value, set = origin.Writers.TryGetValue(p.Key, out var w) ? w.Tokens.Skip(1).Take(16).ToArray() : null }).Where(p => p.set != null || CommandFits(state.Class, p.command)).ToArray()
            });
            object? Instruction(SourceInstruction? i) => i == null ? null : new { script = i.Script, line = i.Line, command = i.Command, tokens = i.Tokens.Take(16).Select(t => Bounded(t, 128)).ToArray(), runs = d.SourceBuild!.Executions.GetValueOrDefault((i.Script, i.Line)) };
        });
        RegisterJob(r, "source_world_command", "Set a property a script command makes on a node of a source world (fog of the world, a light's color, ranges or orientation, a camera's clip or field of view) as one undoable change of the project's workspace: the instruction that last set it changes, or a new one follows the instruction that created the node. zstudio_source_world_object lists the commands that fit the node and the arguments each set one has. The world rebuilds and the result is the replacement document.",
            [DocumentParameter, RevisionParameter, new("node", "integer", "Scene node index.", true, Minimum: 0, Maximum: int.MaxValue),
             P("command", "string", "Property command.", true, [.. SourceObjectEdits.PropertyCommands.Keys]),
             new("arguments", "array", "The command's arguments as script tokens (numbers as text).", true, Items: new("", "string", "One argument."), MinItems: 1, MaxItems: 8)], true,
            async (a, token) =>
            {
                var d = TargetDocument(a, true);
                var args = (a["arguments"] as JsonArray)!.Select(v => v!.GetValue<string>()).ToArray();
                return Result(new { document = DocumentState(await CommandSourceObjectAsync(d, Int(a, "node"), Text(a, "command"), args, token)) });
            });
        RegisterJob(r, "source_world_object_edit", "Edit a world object of a source world as one undoable change of the project's workspace: move, rotate or scale it, set or clear one of its node flags, copy it, delete it, or move it under another parent. The edit changes the source that placed the object: the transform or extras of its glTF node (the mission database, or a model file, which every load of it uses), or the script instruction that set the value (refused when it ran more than once or takes the value from a macro); a value nothing set yet is added as an instruction after the one that created the object. Copying, deleting and re-parenting apply to the whole object: a node of a model a script loaded stands for that load (zstudio_source_world_object names it). A mission database object is copied, deleted or moved in its glTF file; a script's object is deleted by turning its instructions into comments, copied by loading it again before the world is written, and moved by attaching it there. The world rebuilds and the result is the replacement document (with the copy's node index); a change the world cannot be built with is taken back.",
            [DocumentParameter, RevisionParameter, new("node", "integer", "Scene node index.", true, Minimum: 0, Maximum: int.MaxValue),
             P("action", "string", "What to do; inferred from the other arguments when omitted (a transform or a flag).", false, ["transform", "flag", "duplicate", "delete", "parent"]),
             new("position", "object", "New local position (or the copy's).", Properties: [P("x", "number", "X.", true), P("y", "number", "Y.", true), P("z", "number", "Z.", true)]),
             new("rotationDegrees", "object", "New local rotation in degrees (about Y, then X, then Z, as Object3DRotate).", Properties: [P("x", "number", "X.", true), P("y", "number", "Y.", true), P("z", "number", "Z.", true)]),
             new("scale", "object", "New local scale.", Properties: [P("x", "number", "X.", true), P("y", "number", "Y.", true), P("z", "number", "Z.", true)]),
             P("flag", "string", "Node flag to set or clear.", false, SourceObjectPropertiesEditor.EditableFlags.Select(f => $"0x{f.Bit:X}").ToArray()),
             P("on", "boolean", "Whether the flag is set; required with flag."),
             new("name", "string", "The copy's node name (duplicate): 1–32 printable characters without spaces, commas, quotes or # % ;, used by no node of the world."),
             new("parent", "integer", "The new parent's scene node index (parent); -1 or the world's node makes it a root of the world.", Minimum: -1, Maximum: int.MaxValue)], true,
            async (a, token) =>
            {
                var d = TargetDocument(a, true); int node = Int(a, "node");
                bool moves = a["position"] != null || a["rotationDegrees"] != null || a["scale"] != null;
                string action = a["action"] is null ? (moves ? "transform" : "flag") : Text(a, "action");
                DocumentModel next;
                if (action == "duplicate")
                {
                    if (a["name"] is null) throw new StudioCommandException("invalid_argument", "Give the copy's name.");
                    string name = Text(a, "name");
                    ObjectTransform? placed = null;
                    if (moves)
                    {
                        // Omitted values come from the object copied (a part stands for the object that loaded it).
                        var whole = SourceObjectTargetFor(d, node, SourceWorldOf(d).Workspace).Node;
                        int wholeNode = SourceWorldModel(d).Slots.First(p => ReferenceEquals(p.Value, whole)).Key;
                        var current = DescribeSourceObject(d, wholeNode).Transform ?? throw new StudioCommandException("unsupported", "Only object nodes have a transform.");
                        placed = new(Vector(a, "position") ?? current.Position, Vector(a, "rotationDegrees") ?? current.RotationDegrees, Vector(a, "scale") ?? current.Scale);
                    }
                    next = await DuplicateSourceObjectAsync(d, node, name, placed, token);
                    return Result(new { document = DocumentState(next), copy = SourceNodeNamed(next, name) });
                }
                if (action == "delete") return Result(new { document = DocumentState(await DeleteSourceObjectAsync(d, node, token)) });
                if (action == "parent")
                {
                    if (a["parent"] is null) throw new StudioCommandException("invalid_argument", "Give the new parent's node index, or -1 for the world.");
                    int parent = Int(a, "parent");
                    return Result(new { document = DocumentState(await ReparentSourceObjectAsync(d, node, parent < 0 ? null : parent, token)) });
                }
                if ((action == "transform") != moves || (action == "flag") != (a["flag"] != null)) throw new StudioCommandException("invalid_argument", "Give a transform (position, rotationDegrees, scale) or one flag.");
                if (moves)
                {
                    var current = DescribeSourceObject(d, node).Transform ?? throw new StudioCommandException("unsupported", "Only object nodes have a transform.");
                    ObjectTransform requested = new(Vector(a, "position") ?? current.Position, Vector(a, "rotationDegrees") ?? current.RotationDegrees, Vector(a, "scale") ?? current.Scale);
                    next = await MoveSourceObjectAsync(d, node, requested, token);
                }
                else
                {
                    if (a["on"] == null) throw new StudioCommandException("invalid_argument", "Give on with flag.");
                    uint bit = Convert.ToUInt32(Text(a, "flag")[2..], 16);
                    next = await FlagSourceObjectAsync(d, node, bit, Flag(a, "on"), token);
                }
                return Result(new { document = DocumentState(next) });
            });
    }
    /// <summary>Whether a property command applies to a node class (the interpreter applies World… to worlds, Light… to lights, and so on).</summary>
    private static bool CommandFits(string nodeClass, string command) => command.StartsWith(nodeClass switch { "World" => "World", "Light" => "Light", "Camera" => "Camera", "Display" => "Display", _ => "\0" }, StringComparison.Ordinal);
    private static Vector3? Vector(JsonObject a, string name) => a[name] is JsonObject v ? new Vector3(Coordinate(v, "x"), Coordinate(v, "y"), Coordinate(v, "z")) : null;
}
