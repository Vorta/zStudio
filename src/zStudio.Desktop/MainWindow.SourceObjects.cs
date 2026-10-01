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
    private sealed record SourceWorldModelEntry(GameZWorld World, IReadOnlyDictionary<int, WorldNode> Slots);
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<DocumentModel, SourceWorldModelEntry> sourceWorldModels = new();
    private SourceWorldModelEntry SourceWorldModel(DocumentModel doc)
    {
        if (sourceWorldModels.TryGetValue(doc, out var cached)) return cached;
        var world = GameZWorldReader.FromDocument(doc.Document, doc.Lifetime.Token);
        SourceWorldModelEntry model = new(world, GameZWriter.NodeSlots(world).ToDictionary(p => p.Value, p => p.Key));
        sourceWorldModels.AddOrUpdate(doc, model);
        return model;
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
        return new(node, built.Name, built.Class.ToString(), transform, built.Flags, origin, source, notes);
    }

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
        EditSourceObjectAsync(doc, node, (w, s, e) => SourceObjectEdits.PlanTransform(w, s.Name, s.Origin, e, transform, token), token);
    private Task<DocumentModel> FlagSourceObjectAsync(DocumentModel doc, int node, uint bit, bool on, CancellationToken token) =>
        EditSourceObjectAsync(doc, node, (w, s, e) => SourceObjectEdits.PlanFlag(w, s.Name, s.Origin, e, bit, on, token), token);

    /// <summary>Opens Properties for a source world's object; after an edit it follows the object into the rebuilt world.</summary>
    private bool ShowSourceObjectProperties(DocumentModel doc, int node)
    {
        var state = DescribeSourceObject(doc, node);
        var window = GetPropertiesWindow();
        SourceObjectPropertiesEditor fields = new(state,
            transform => FollowSourceObjectAsync(state, () => MoveSourceObjectAsync(doc, node, transform, CancellationToken.None)),
            (bit, on) => FollowSourceObjectAsync(state, () => FlagSourceObjectAsync(doc, node, bit, on, CancellationToken.None)));
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
                editableFlags = SourceObjectPropertiesEditor.EditableFlags.Select(f => new { bit = $"0x{f.Bit:X}", label = f.Label, on = (state.Flags & f.Bit) != 0 }).ToArray()
            });
            object? Instruction(SourceInstruction? i) => i == null ? null : new { script = i.Script, line = i.Line, command = i.Command, tokens = i.Tokens.Take(16).Select(t => Bounded(t, 128)).ToArray(), runs = d.SourceBuild!.Executions.GetValueOrDefault((i.Script, i.Line)) };
        });
        RegisterJob(r, "source_world_object_edit", "Move, rotate or scale a world object of a source world, or set or clear one of its node flags, as one undoable change of the project's workspace. The edit changes the source that placed the object: the transform or extras of its glTF node (the mission database, or a model file, which every load of it uses), or the script instruction that set the value (refused when it ran more than once or takes the value from a macro); a value nothing set yet is added as an instruction after the one that created the object. The world rebuilds and the result is the replacement document; a change the world cannot be built with is taken back.",
            [DocumentParameter, RevisionParameter, new("node", "integer", "Scene node index.", true, Minimum: 0, Maximum: int.MaxValue),
             new("position", "object", "New local position.", Properties: [P("x", "number", "X.", true), P("y", "number", "Y.", true), P("z", "number", "Z.", true)]),
             new("rotationDegrees", "object", "New local rotation in degrees (about Y, then X, then Z, as Object3DRotate).", Properties: [P("x", "number", "X.", true), P("y", "number", "Y.", true), P("z", "number", "Z.", true)]),
             new("scale", "object", "New local scale.", Properties: [P("x", "number", "X.", true), P("y", "number", "Y.", true), P("z", "number", "Z.", true)]),
             P("flag", "string", "Node flag to set or clear.", false, SourceObjectPropertiesEditor.EditableFlags.Select(f => $"0x{f.Bit:X}").ToArray()),
             P("on", "boolean", "Whether the flag is set; required with flag.")], true,
            async (a, token) =>
            {
                var d = TargetDocument(a, true); int node = Int(a, "node");
                bool moves = a["position"] != null || a["rotationDegrees"] != null || a["scale"] != null;
                if (moves == (a["flag"] != null)) throw new StudioCommandException("invalid_argument", "Give a transform (position, rotationDegrees, scale) or one flag.");
                DocumentModel next;
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
    private static Vector3? Vector(JsonObject a, string name) => a[name] is JsonObject v ? new Vector3(Coordinate(v, "x"), Coordinate(v, "y"), Coordinate(v, "z")) : null;
}
