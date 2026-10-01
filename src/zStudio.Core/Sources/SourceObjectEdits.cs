using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Recoil.Zbd.Core.Gltf;
using Recoil.Zbd.Core.Worlds;

namespace Recoil.Zbd.Core.Sources;

/// <summary>A world object's local transform: position, rotation in degrees as Object3DRotate takes it (about Y, then X, then Z) and scale.</summary>
public readonly record struct ObjectTransform(Vector3 Position, Vector3 RotationDegrees, Vector3 Scale)
{
    public static ObjectTransform Identity => new(Vector3.Zero, Vector3.Zero, Vector3.One);
    /// <summary>The engine's local matrix for this transform (MatApplyLocalTRS: R = Ry·Rx·Rz, scaled rows, then the translation).</summary>
    public Matrix4x4 Matrix()
    {
        Vector3 r = RotationDegrees * (MathF.PI / 180f);
        var m = Matrix4x4.CreateScale(Scale) * Matrix4x4.CreateRotationZ(r.Z) * Matrix4x4.CreateRotationX(r.X) * Matrix4x4.CreateRotationY(r.Y);
        m.Translation = Position;
        return m;
    }
    /// <summary>
    /// A node's transform as its sources hold it. A matrix the build composed from Object3DTranslate, Object3DRotate and
    /// Object3DScale (no authored matrix) is shown as those stored values, so an edit of one keeps the others exactly (and
    /// the Euler angles and scale animations start from); an authored or loaded matrix is decomposed.
    /// </summary>
    public static ObjectTransform Of(WorldNode node)
    {
        if (WorldUpdate.LocalMatrix(node) is not { } m) return Identity;
        if ((node.PayloadInt(0) & 0x10) == 0)
        {
            const float degrees = 180f / MathF.PI;
            ObjectTransform stored = new(m.Translation,
                Snap(new Vector3(node.PayloadFloat(0x18), node.PayloadFloat(0x1C), node.PayloadFloat(0x20)) * degrees, angles: true),
                new(node.PayloadFloat(0x24), node.PayloadFloat(0x28), node.PayloadFloat(0x2C)));
            // Only when they compose the matrix (the build keeps them together; another program might not). Each row compares
            // relative to its own length, so a large scale's rounding does not count as a difference.
            var composed = stored.Matrix();
            bool same = true;
            for (int i = 0; i < 4 && same; i++)
            {
                float length = MathF.Sqrt(m[i, 0] * m[i, 0] + m[i, 1] * m[i, 1] + m[i, 2] * m[i, 2]);
                for (int j = 0; j < 3 && same; j++) same = MathF.Abs(composed[i, j] - m[i, j]) <= 1e-4f * MathF.Max(1, length);
            }
            if (same) return stored;
        }
        var decomposed = FromMatrix(m);
        return decomposed with { RotationDegrees = Snap(decomposed.RotationDegrees, angles: true), Scale = Snap(decomposed.Scale, angles: false) };
    }
    /// <summary>
    /// Removes the float noise a conversion leaves (45.000004°, a scale of 0.99999994, an angle of 5e-6°): values within a
    /// millionth of a 3-decimal value take it. Authored values such as a scale of 1.00005 stay.
    /// </summary>
    internal static Vector3 Snap(Vector3 v, bool angles) => new(SnapOne(v.X, angles), SnapOne(v.Y, angles), SnapOne(v.Z, angles));
    private static float SnapOne(float x, bool angles)
    {
        if (angles && MathF.Abs(x) < 1e-4f) return 0;
        float rounded = MathF.Round(x, 3);
        return MathF.Abs(x) >= 1e-3f && MathF.Abs(x - rounded) <= 1e-6f * MathF.Max(1, MathF.Abs(x)) ? rounded : x;
    }
    /// <summary>
    /// The transform a local matrix (rows: rotated, scaled axes, then translation) describes. A mirroring matrix has a
    /// negative X scale, so <see cref="Matrix"/> gives it back.
    /// </summary>
    public static ObjectTransform FromMatrix(Matrix4x4 m)
    {
        Vector3 x = new(m.M11, m.M12, m.M13), y = new(m.M21, m.M22, m.M23), z = new(m.M31, m.M32, m.M33);
        Vector3 scale = new(x.Length(), y.Length(), z.Length());
        if (scale.X == 0 || scale.Y == 0 || scale.Z == 0) return new(m.Translation, Vector3.Zero, scale);
        if (Vector3.Dot(Vector3.Cross(x, y), z) < 0) scale.X = -scale.X;
        x /= scale.X; y /= scale.Y; z /= scale.Z;
        // Rows of Rz·Rx·Ry: M32 = −sin x, M31/M33 give y, M12/M22 give z (gimbal lock: z is 0).
        float rx = MathF.Asin(Math.Clamp(-z.Y, -1f, 1f)), ry, rz;
        if (MathF.Abs(MathF.Cos(rx)) > 1e-6f) { ry = MathF.Atan2(z.X, z.Z); rz = MathF.Atan2(x.Y, y.Y); }
        else { rz = 0; ry = MathF.Atan2(-x.Z, x.X); }
        const float degrees = 180f / MathF.PI;
        return new(m.Translation, new Vector3(rx, ry, rz) * degrees, scale);
    }
}

/// <summary>A planned change of a project's sources: the label for history, the files and their new content, what it edits and notes on its reach.</summary>
public sealed record SourceEditPlan(string Label, IReadOnlyList<(string Relative, byte[] Content)> Changes, string Target, IReadOnlyList<string> Notes)
{
    /// <summary>Models the change loads right before the world is written, which the rebuild checks it attached as planned.</summary>
    public IReadOnlyList<SourceModelAddition> Additions { get; init; } = [];
}

/// <summary>An object of a built source world, with the world and provenance a structural edit of it is checked against.</summary>
public sealed record SourceObjectTarget(SourceWorkspace Workspace, string Mission, GameZWorld World, WorldNode Node, IReadOnlyDictionary<WorldNode, WorldNodeProvenance> Provenance, IReadOnlyDictionary<(string Script, int Line), int> Executions)
{
    /// <summary>The GameZWriteZBDFile the build ran; lines added before the world is written go before it.</summary>
    public SourceInstruction? Write { get; init; }
    public WorldNodeProvenance Origin => Provenance.TryGetValue(Node, out var origin) ? origin : throw new InvalidDataException($"{Node.Name} has no recorded source.");
}

/// <summary>
/// Plans edits of a world's objects as changes of the sources that made them (see <see cref="WorldNodeProvenance"/>): a
/// value a script instruction set is changed in that instruction, a node imported from a glTF file in that file, and a
/// value nothing set yet is added by a new instruction right after the one that created the node. An instruction that
/// ran more than once, or whose value comes from a macro, cannot be edited for one object and is refused.
/// </summary>
public static class SourceObjectEdits
{
    private static readonly string[] TransformCommands = ["Object3DTranslate", "Object3DRotate", "Object3DScale"];
    /// <summary>Script commands that set a property of the node they apply to (fog, lights, cameras, windows), with the arguments each takes.</summary>
    public static readonly IReadOnlyDictionary<string, string> PropertyCommands = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["WorldSetFogState"] = "linear, exponential or off", ["WorldSetFogColor"] = "red green blue (0–1)", ["WorldSetFogRange"] = "start end",
        ["WorldSetFogAltitude"] = "low high", ["WorldSetFogDensity"] = "density",
        ["LightSetColor"] = "red green blue", ["LightSetDiffuse"] = "diffuse", ["LightSetAmbient"] = "ambient", ["LightSetRanges"] = "near far",
        ["LightSetOrientation"] = "x y z degrees", ["LightSetTranslate"] = "x y z", ["LightSetActive"] = "on or off", ["LightSetSaturated"] = "on or off",
        ["CameraSetNearFarClip"] = "near far", ["CameraSetFOV"] = "horizontal vertical degrees", ["CameraSetLODMultiplier"] = "multiplier",
        ["DisplaySetClearColor"] = "red green blue",
    };
    /// <summary>Node flag bits and the script command that sets each.</summary>
    public static readonly IReadOnlyDictionary<uint, string> FlagCommands = new Dictionary<uint, string>
    {
        [0x08] = "SetAltitudeSurface", [0x10] = "SetIntersectSurface", [0x20] = "SetIntersectBBOX", [0x40] = "SetProximity", [0x80] = "SetLandmark",
        [0x10000] = "NodeSetCanModify", [0x800000] = "NodeSetOverwrite",
    };

    /// <summary>
    /// A plan that moves, rotates or scales an object to <paramref name="requested"/> (its local transform). With
    /// <paramref name="current"/> (the transform shown, <see cref="ObjectTransform.Of"/>), only the components that differ
    /// from it change: a position edit leaves the authored rotation and scale (and a mirroring the decomposition cannot
    /// express) as they are, and a script keeps the tokens of every component not changed.
    /// </summary>
    public static SourceEditPlan PlanTransform(SourceWorkspace workspace, string nodeName, WorldNodeProvenance origin, IReadOnlyDictionary<(string Script, int Line), int> executions, ObjectTransform requested, CancellationToken token = default, string? mission = null, ObjectTransform? current = null)
    {
        Generated(origin, nodeName);
        Check(requested.Position); Check(requested.RotationDegrees); Check(requested.Scale);
        string label = $"Move {nodeName}";
        bool position = current is not { } c || Differs(requested.Position, c.Position);
        bool rotation = current is not { } r || Differs(requested.RotationDegrees, r.RotationDegrees);
        bool scale = current is not { } s || Differs(requested.Scale, s.Scale);
        if (!position && !rotation && !scale) return new(label, [], nodeName, []);
        var writers = TransformCommands.Where(origin.Writers.ContainsKey).Select(c => origin.Writers[c]).ToArray();
        // A glTF node imported without a transform takes the scripts' TRS whole; one with its own (authored) matrix keeps its
        // rotation and scale, while a script Object3DTranslate still sets its translation.
        if (origin.ModelFile == null || writers.Length > 0 && !origin.ModelTransformAuthored)
        {
            // The scripts place the object: change its transform instructions, adding those it lacks after the last one (or after the
            // instruction that created the object, which leaves it current).
            var anchor = writers.OrderBy(w => w.Line).LastOrDefault() ?? origin.Created ?? throw new InvalidDataException($"{nodeName} was neither loaded from a model nor created by a script instruction.");
            if (writers.Select(w => w.Script).Append(anchor.Script).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
                throw new InvalidDataException($"{nodeName}'s transform is set in several scripts; edit them in the scripts directly.");
            ScriptEdit edit = new(workspace, anchor.Script, executions, token, mission);
            // Shown as the scripts store them (ObjectTransform.Of), each value changes alone; components the request keeps
            // keep their tokens.
            if (position) edit.Set(origin.Writers.GetValueOrDefault("Object3DTranslate"), "Object3DTranslate", requested.Position, Vector3.Zero, anchor, current?.Position);
            if (rotation) edit.Set(origin.Writers.GetValueOrDefault("Object3DRotate"), "Object3DRotate", requested.RotationDegrees, Vector3.Zero, anchor, current?.RotationDegrees);
            if (scale) edit.Set(origin.Writers.GetValueOrDefault("Object3DScale"), "Object3DScale", requested.Scale, Vector3.One, anchor, current?.Scale);
            return new(label, edit.Changes(), $"{anchor.Script} line {anchor.Line}", edit.Notes);
        }
        // A node of a glTF file: its transform is the node's, except a translation a script sets.
        List<(string Relative, byte[] Content)> changes = []; List<string> notes = []; List<string> places = [];
        var translate = origin.Writers.GetValueOrDefault("Object3DTranslate");
        if (position && translate != null)
        {
            ScriptEdit edit = new(workspace, translate.Script, executions, token, mission);
            edit.Set(translate, "Object3DTranslate", requested.Position, Vector3.Zero, translate, current?.Position);
            changes.AddRange(edit.Changes()); notes.AddRange(edit.Notes); places.Add($"{translate.Script} line {translate.Line}");
        }
        bool gltfPosition = position && translate == null;
        if (rotation || scale || gltfPosition)
        {
            // Script rotation and scale are ignored only while the node's matrix is authored; an identity matrix would bring them back.
            bool scriptBasis = origin.Writers.ContainsKey("Object3DRotate") || origin.Writers.ContainsKey("Object3DScale");
            var plan = GltfEdit(workspace, origin, label, node =>
            {
                var local = GltfNodeEdits.Local(node);
                var m = local;
                // Values a decomposition leaves a hair off (89.99999°, a scale of 0.99999994) are written as meant.
                if (rotation || scale) { m = new ObjectTransform(requested.Position, ObjectTransform.Snap(requested.RotationDegrees, angles: true), ObjectTransform.Snap(requested.Scale, angles: false)).Matrix(); m.Translation = local.Translation; }
                if (gltfPosition) m.Translation = requested.Position;
                if (m.IsIdentity && scriptBasis)
                    throw new InvalidDataException($"An identity transform would let {nodeName}'s script Object3DRotate or Object3DScale apply again; change those instructions instead.");
                // A move keeps the authored basis as written; anything else writes the new transform.
                if (!rotation && !scale && node["matrix"] is not JsonArray) node["translation"] = Array(m.Translation.X, m.Translation.Y, m.Translation.Z);
                else GltfNodeEdits.SetLocal(node, m);
            }, token);
            changes.AddRange(plan.Changes); notes.AddRange(plan.Notes); places.Add(plan.Target);
        }
        return new(label, changes, string.Join("; ", places), notes);
    }

    /// <summary>A plan that sets or clears one node flag bit (see <see cref="FlagCommands"/> and <see cref="WorldGltf.CarriedFlags"/>).</summary>
    public static SourceEditPlan PlanFlag(SourceWorkspace workspace, string nodeName, WorldNodeProvenance origin, IReadOnlyDictionary<(string Script, int Line), int> executions, uint bit, bool on, CancellationToken token = default, string? mission = null)
    {
        Generated(origin, nodeName);
        if (System.Numerics.BitOperations.PopCount(bit) != 1 || (bit & WorldGltf.CarriedFlags) == 0) throw new InvalidDataException($"0x{bit:X} is not one node flag a source can set.");
        string label = $"{(on ? "Set" : "Clear")} flag 0x{bit:X} of {nodeName}";
        string value = on ? "on" : "off";
        if (FlagCommands.TryGetValue(bit, out string? command) && origin.Writers.TryGetValue(command, out var writer))
        {
            ScriptEdit edit = new(workspace, writer.Script, executions, token, mission);
            edit.Replace(writer, new() { [1] = value });
            return new(label, edit.Changes(), $"{writer.Script} line {writer.Line}", edit.Notes);
        }
        if (origin.ModelFile != null)
            return GltfEdit(workspace, origin, label, node =>
            {
                var extras = node["extras"] as JsonObject ?? (JsonObject)(node["extras"] = new JsonObject());
                var recoil = extras[WorldGltf.Key] as JsonObject ?? (JsonObject)(extras[WorldGltf.Key] = new JsonObject());
                // As the importer reads it: hexadecimal, with or without 0x.
                uint carried = recoil["flags"] is JsonValue text && text.TryGetValue<string>(out var hex)
                    && uint.TryParse(hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? hex.AsSpan(2) : hex.AsSpan(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint parsed) ? parsed & WorldGltf.CarriedFlags : WorldGltf.DefaultCarried;
                carried = on ? carried | bit : carried & ~bit;
                if (carried == WorldGltf.DefaultCarried) recoil.Remove("flags"); else recoil["flags"] = $"0x{carried:X8}";
            }, token);
        if (command == null) throw new InvalidDataException($"No script command sets flag 0x{bit:X}; {nodeName} was created by a script.");
        var anchor = origin.Created ?? throw new InvalidDataException($"{nodeName} was neither loaded from a model nor created by a script instruction.");
        ScriptEdit insert = new(workspace, anchor.Script, executions, token, mission);
        insert.Insert(anchor, [command, value]);
        return new(label, insert.Changes(), $"{anchor.Script} line {anchor.Line}", insert.Notes);
    }

    /// <summary>
    /// A plan that sets the arguments of one property command (WorldSetFogColor, LightSetDiffuse, …) of a node a script made:
    /// the instruction that last set it changes, or a new one follows the instruction that created the node.
    /// </summary>
    public static SourceEditPlan PlanCommand(SourceWorkspace workspace, string nodeName, WorldNodeProvenance origin, IReadOnlyDictionary<(string Script, int Line), int> executions, string command, IReadOnlyList<string> args, CancellationToken token = default, string? mission = null)
    {
        Generated(origin, nodeName);
        if (args.Count == 0) throw new InvalidDataException($"{command} needs arguments.");
        string label = $"{command} on {nodeName}";
        if (origin.Writers.TryGetValue(command, out var writer))
        {
            ScriptEdit edit = new(workspace, writer.Script, executions, token, mission);
            if (writer.Tokens.Count - 1 != args.Count) edit.ReplaceLine(writer, [command, .. args]);
            else edit.Replace(writer, args.Select((a, i) => (Index: i + 1, Value: a)).ToDictionary(p => p.Index, p => p.Value));
            return new(label, edit.Changes(), $"{writer.Script} line {writer.Line}", edit.Notes);
        }
        var anchor = origin.Created ?? throw new InvalidDataException($"No instruction sets {command} for {nodeName}, and no script created it.");
        ScriptEdit insert = new(workspace, anchor.Script, executions, token, mission);
        insert.Insert(anchor, [command, .. args]);
        return new(label, insert.Changes(), $"{anchor.Script} line {anchor.Line}", insert.Notes);
    }

    /// <summary>
    /// The node that stands for a whole object in structural edits. A node of a model file is part of whatever loaded the
    /// file: the mission database node that references it, or the root a script's LoadGameGen created. Anything else
    /// stands for itself.
    /// </summary>
    public static WorldNode ObjectOf(WorldNode node, IReadOnlyDictionary<WorldNode, WorldNodeProvenance> provenance)
    {
        if (!provenance.TryGetValue(node, out var origin) || origin.ModelFile == null || origin.Database || origin.Load is not { } load) return node;
        HashSet<WorldNode> seen = new(ReferenceEqualityComparer.Instance); Queue<WorldNode> pending = new(node.Parents);
        while (pending.TryDequeue(out var parent))
        {
            if (!seen.Add(parent) || !provenance.TryGetValue(parent, out var p)) continue;
            if (p.Database || p.LoadedFile != null && Same(p.Created, load)) return parent;
            foreach (var up in parent.Parents) pending.Enqueue(up);
        }
        return node;
    }

    /// <summary>
    /// A plan that deletes an object: a node of the mission database leaves its glTF file with its descendants; an object a
    /// script created has every instruction that created, changed or attached it turned into a comment, so the rest of the
    /// script runs as before. Objects other instructions use (a camera's horizon, a world's light) are refused.
    /// </summary>
    public static SourceEditPlan PlanDelete(SourceObjectTarget target, CancellationToken token = default)
    {
        var node = target.Node; var origin = target.Origin;
        Generated(origin, node.Name);
        string label = $"Delete {node.Name}";
        if (node.Class is not (WorldNodeClass.Object3D or WorldNodeClass.Lod)) throw new InvalidDataException($"{node.Name} is a {node.Class} node; only objects can be deleted here.");
        List<string> notes = [$"Animations and resources that find {node.Name} by name no longer find it; Problems lists what the rebuild reports."];
        if (node.Parents.Count > 1) throw new InvalidDataException($"{node.Name} has several parents (a shared node); delete it in its source directly.");
        if (origin.ModelFile != null)
        {
            if (!origin.Database) throw new InvalidDataException($"{node.Name} is part of the model file {origin.ModelFile}; delete the object that loads it, or remove the part in Blender.");
            // A script instruction that acts on the node or a part of it would act on another node, or none, once it is gone.
            RefuseUsers(target, Subtree(node), node.Name, "deleted");
            return GltfFile(target.Workspace, origin, label, (root, _) => GltfNodeEdits.Remove(root, origin.ModelNode), token, notes);
        }
        var created = Created(target, ["LoadGameGen", "NewObject3D"], "deleted");
        // Instructions on the object itself become comments (an AddChild of a part with them); others below it refuse.
        RefuseUsers(target, Subtree(node).Where(n => !ReferenceEquals(n, node)), node.Name, "deleted", origin.Applied.Concat(origin.Named).Prepend(created));
        if (origin.Named.FirstOrDefault(n => n.Command is not ("AddChild" or "DeleteChild" or "DeleteTree")) is { } user)
            throw new InvalidDataException($"{user.Script} line {user.Line} ({user.Command}) uses {node.Name}; change that instruction first.");
        ScriptEdit edit = new(target.Workspace, created.Script, target.Executions, token, target.Mission);
        foreach (var instruction in origin.Applied)
        {
            if (!Disableable.Contains(instruction.Command)) throw new InvalidDataException($"{instruction.Script} line {instruction.Line} ({instruction.Command}) acts on {node.Name} in a way a deletion cannot take out; edit the script directly.");
            if (instruction.Command == "AddChild" && instruction.Args.Count > 0) notes.Add($"{instruction.Args[0]} was attached to {node.Name} and is no longer in the world.");
        }
        foreach (var instruction in origin.Applied.Concat(origin.Named).Prepend(created)) edit.Comment(instruction);
        return new(label, edit.Changes(), $"{created.Script} line {created.Line}", notes);
    }

    /// <summary>
    /// A plan that copies an object under the same parent, named <paramref name="name"/>, at <paramref name="transform"/>
    /// (its local transform; the original's when null). A mission database node is copied in its glTF file (sharing its
    /// meshes); an object a script loaded is loaded again by lines added before the world is written, repeating the
    /// instructions that set it up.
    /// </summary>
    /// <remarks>
    /// With <paramref name="keepBasis"/>, a glTF copy keeps the original's built rotation, scale and any mirroring, and takes
    /// only the position of <paramref name="transform"/>.
    /// </remarks>
    public static SourceEditPlan PlanDuplicate(SourceObjectTarget target, string name, ObjectTransform? transform, CancellationToken token = default, bool keepBasis = false)
    {
        var node = target.Node; var origin = target.Origin;
        Generated(origin, node.Name);
        CheckName(name);
        if (node.Class is not (WorldNodeClass.Object3D or WorldNodeClass.Lod)) throw new InvalidDataException($"{node.Name} is a {node.Class} node; only objects can be copied here.");
        if (target.World.Nodes.Any(n => n.Name == name)) throw new InvalidDataException($"The world already has a node named {name}; choose another name.");
        if (transform != null) { Check(transform.Value.Position); Check(transform.Value.RotationDegrees); Check(transform.Value.Scale); }
        string label = $"Copy {node.Name} as {name}";
        string parts = $"The copy's parts keep the original's part names; an animation that finds a part by name may find the copy's.";
        if (origin.ModelFile != null)
        {
            if (!origin.Database) throw new InvalidDataException($"{node.Name} is part of the model file {origin.ModelFile}; copy the object that loads it.");
            // The copy's parts are newer and keep their names: a script finding one by name would find the copy's.
            RefuseUsers(target, Subtree(node).Where(n => !ReferenceEquals(n, node)), node.Name, "copied");
            // Script instructions find the original by name, so the copy gets what they set from the build instead: its
            // place (as an authored matrix) and its flags. Other instructions do not reach it.
            var built = WorldUpdate.LocalMatrix(node) ?? Matrix4x4.Identity;
            Matrix4x4? local = transform is { } t ? keepBasis ? built with { Translation = t.Position } : t.Matrix()
                : TransformCommands.Any(origin.Writers.ContainsKey) ? built : null;
            bool flags = origin.Applied.Any(i => FlagCommands.Values.Contains(i.Command));
            List<string> copyNotes = ["The copy shares the original's meshes and textures.", parts];
            foreach (var i in origin.Applied.Where(i => !TransformCommands.Contains(i.Command) && !FlagCommands.Values.Contains(i.Command) && i.Command is not ("NodeSetLighting" or "FindSubNode")))
                copyNotes.Add($"{i.Script} line {i.Line} ({i.Command}) acts on {node.Name} by name; the copy does not get it.");
            return GltfFile(target.Workspace, origin, label, (root, _) =>
            {
                int copy = GltfNodeEdits.Duplicate(root, origin.ModelNode, name);
                var copied = (JsonObject)root["nodes"]![copy]!;
                if (local is { } m) GltfNodeEdits.SetLocal(copied, m);
                if (flags) ((JsonObject)copied["extras"]![WorldGltf.Key]!)["flags"] = $"0x{node.Flags & WorldGltf.CarriedFlags:X8}";
            }, token, copyNotes);
        }
        var created = Created(target, ["LoadGameGen"], "copied");
        string file = origin.LoadedFile ?? throw new InvalidDataException($"{node.Name}'s LoadGameGen found no model file.");
        var parent = SingleParent(target, node, "copied");
        List<IReadOnlyList<string>> lines = [["SetModelDirectory", "..\\" + Path.GetDirectoryName(file.Replace('\\', '/'))!.Replace('/', '\\')], ["LoadGameGen", Token(created.Args[0]), name]];
        List<string> notes = [parts];
        foreach (var instruction in origin.Applied)
        {
            if (TransformCommands.Contains(instruction.Command) && transform != null) continue;
            if (instruction.Command == "AddChild") { notes.Add($"{(instruction.Args.Count > 0 ? instruction.Args[0] : "A node")}, which a script attached to {node.Name}, is not copied."); continue; }
            if (!Copyable.Contains(instruction.Command)) throw new InvalidDataException($"{instruction.Script} line {instruction.Line} ({instruction.Command}) acts on {node.Name} in a way a copy cannot repeat; copy it in the script.");
            lines.Add([instruction.Command, .. instruction.Args.Select(Token)]);
        }
        if (transform is { } placed)
        {
            lines.Add(["Object3DTranslate", Number(placed.Position.X), Number(placed.Position.Y), Number(placed.Position.Z)]);
            if (placed.RotationDegrees != Vector3.Zero) lines.Add(["Object3DRotate", Number(placed.RotationDegrees.X), Number(placed.RotationDegrees.Y), Number(placed.RotationDegrees.Z)]);
            if (placed.Scale != Vector3.One) lines.Add(["Object3DScale", Number(placed.Scale.X), Number(placed.Scale.Y), Number(placed.Scale.Z)]);
        }
        lines.Add(["FindNode", parent.Name]); lines.Add(["AddChild", name]);
        ScriptEdit edit = new(target.Workspace, SourceBuilder.WorldScript(target.Mission), target.Executions, token, target.Mission);
        edit.InsertBeforeWrite(lines, target.Write);
        return new(label, edit.Changes(), $"{SourceBuilder.WorldScript(target.Mission)}, before the world is written", notes)
        {
            Additions = [new(file, name, parent.Class == WorldNodeClass.World ? Vector3.Zero : null)],
        };
    }

    /// <summary>
    /// A plan that moves an object under <paramref name="parent"/> (null: a root of the mission database, which joins the
    /// world), keeping its place in the world. A mission database node moves within its glTF file under another node of
    /// that file; an object a script attached is attached by lines added before the world is written instead, its old
    /// AddChild turned into a comment and its transform changed so it stays where it was.
    /// </summary>
    public static SourceEditPlan PlanReparent(SourceObjectTarget target, WorldNode? parent, CancellationToken token = default)
    {
        var node = target.Node; var origin = target.Origin;
        Generated(origin, node.Name);
        if (node.Class is not (WorldNodeClass.Object3D or WorldNodeClass.Lod)) throw new InvalidDataException($"{node.Name} is a {node.Class} node; only objects can move to another parent here.");
        if (parent != null && (ReferenceEquals(parent, node) || Ancestors(parent).Contains(node))) throw new InvalidDataException($"{node.Name} cannot move under itself or one of its own parts.");
        string label = parent == null ? $"Move {node.Name} to the world" : $"Move {node.Name} under {parent.Name}";
        if (origin.ModelFile != null)
        {
            if (!origin.Database) throw new InvalidDataException($"{node.Name} is part of the model file {origin.ModelFile}; move the object that loads it.");
            int? into = null;
            if (parent != null && parent.Class != WorldNodeClass.World)
            {
                if (!target.Provenance.TryGetValue(parent, out var p) || !p.Database || !string.Equals(p.ModelFile, origin.ModelFile, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"{parent.Name} is not a node of {origin.ModelFile}; a mission database node can only move under another node of the database, or to the world.");
                into = p.ModelNode;
            }
            // The glTF keeps the node's place from the file's transforms; a script transform along either chain moves it
            // elsewhere, and a script that also attaches a node of either chain places an instance the glTF does not.
            var chain = Ancestors(node).Prepend(node).Concat(parent == null ? [] : Ancestors(parent).Prepend(parent)).ToList();
            foreach (var n in chain)
                if (target.Provenance.TryGetValue(n, out var linked) && linked.Named.FirstOrDefault(x => x.Command == "AddChild") is { } attach)
                    throw new InvalidDataException($"{attach.Script} line {attach.Line} also attaches {n.Name} elsewhere; a new place in the glTF would not hold for that instance. Move it in the scripts directly.");
            if (chain.FirstOrDefault(n => target.Provenance.TryGetValue(n, out var p) && TransformCommands.Any(p.Writers.ContainsKey)) is { } scripted)
                throw new InvalidDataException($"A script sets {scripted.Name}'s transform, so the glTF alone cannot keep {node.Name} in place; move it in the scripts directly.");
            return GltfFile(target.Workspace, origin, label, (root, _) => GltfNodeEdits.Reparent(root, origin.ModelNode, into), token,
                [into == null ? $"{node.Name} becomes a root of the mission database, which joins the world and its grid." : $"{node.Name} moves with {parent!.Name} from now on."]);
        }
        var created = Created(target, ["LoadGameGen", "NewObject3D"], "moved");
        var current = SingleParent(target, node, "moved");
        if (parent == null)
        {
            var worlds = target.World.Nodes.Where(n => n.Class == WorldNodeClass.World).Take(2).ToList();
            parent = worlds.Count == 1 ? worlds[0] : throw new InvalidDataException($"Choose the node to move {node.Name} under.");
        }
        if (ReferenceEquals(parent, current)) throw new InvalidDataException($"{node.Name} is already under {parent.Name}.");
        Unique(target, parent.Name, "its new parent");
        Unique(target, node.Name, "it");
        var attached = origin.Named.Where(n => n.Command == "AddChild").ToList();
        ScriptEdit edit = new(target.Workspace, SourceBuilder.WorldScript(target.Mission), target.Executions, token, target.Mission);
        if (!created.Script.Equals(SourceBuilder.WorldScript(target.Mission), StringComparison.OrdinalIgnoreCase) || attached.Any(a => !a.Script.Equals(created.Script, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException($"{node.Name} is set up outside the mission's world script; move it in the scripts directly.");
        foreach (var instruction in attached) edit.Comment(instruction);
        // Keep the world placement: the local transform becomes the old world transform relative to the new parent. With
        // Q = old parent's world × the new parent's inverse, the new local is S·R·T·Q. When Q only moves (the usual case:
        // load roots and the world translate), the rotation and scale stay as stored (the Euler angles and scale
        // animations start from); when Q also turns, the scale stays and only the rotation takes Q's turn.
        if (node.Class == WorldNodeClass.Object3D && Matrix4x4.Invert(WorldMatrix(parent), out var inverse))
        {
            var anchor = TransformCommands.Where(origin.Writers.ContainsKey).Select(c => origin.Writers[c]).OrderBy(w => w.Line).LastOrDefault() ?? created;
            var q = WorldMatrix(current) * inverse;
            var stored = ObjectTransform.Of(node);
            var basis = q with { M41 = 0, M42 = 0, M43 = 0 };
            if (Near(basis, Matrix4x4.Identity))
                edit.Set(origin.Writers.GetValueOrDefault("Object3DTranslate"), "Object3DTranslate", Vector3.Transform(stored.Position, q), Vector3.Zero, anchor, stored.Position);
            else if (Near(basis * Matrix4x4.Transpose(basis), Matrix4x4.Identity) && basis.GetDeterminant() > 0)
            {
                var turned = ObjectTransform.FromMatrix(new ObjectTransform(Vector3.Zero, stored.RotationDegrees, Vector3.One).Matrix() * basis).RotationDegrees;
                edit.Set(origin.Writers.GetValueOrDefault("Object3DTranslate"), "Object3DTranslate", Vector3.Transform(stored.Position, q), Vector3.Zero, anchor, stored.Position);
                edit.Set(origin.Writers.GetValueOrDefault("Object3DRotate"), "Object3DRotate", ObjectTransform.Snap(turned, angles: true), Vector3.Zero, anchor, stored.RotationDegrees);
            }
            else
            {
                // A scaled or mirrored parent change: the transform is decomposed whole.
                var local = ObjectTransform.FromMatrix(WorldMatrix(node) * inverse);
                edit.Set(origin.Writers.GetValueOrDefault("Object3DTranslate"), "Object3DTranslate", local.Position, Vector3.Zero, anchor);
                edit.Set(origin.Writers.GetValueOrDefault("Object3DRotate"), "Object3DRotate", ObjectTransform.Snap(local.RotationDegrees, angles: true), Vector3.Zero, anchor);
                edit.Set(origin.Writers.GetValueOrDefault("Object3DScale"), "Object3DScale", ObjectTransform.Snap(local.Scale, angles: false), Vector3.One, anchor);
            }
        }
        edit.InsertBeforeWrite([["FindNode", parent.Name], ["AddChild", node.Name]], target.Write);
        return new(label, edit.Changes(), $"{created.Script} line {created.Line}", []);
    }

    /// <summary>A terrain piece is compiled from its recipe; its source is the recipe's regions and surfaces, never the piece itself.</summary>
    private static void Generated(WorldNodeProvenance origin, string name)
    {
        if (origin.Terrain is { } recipe) throw new InvalidDataException($"{name} is a piece the terrain recipe {recipe} compiles from surface {origin.TerrainSurface}; change the recipe's regions or the surface instead.");
    }
    private static readonly HashSet<string> Copyable = new(["Object3DTranslate", "Object3DRotate", "Object3DScale", "SetAltitudeSurface", "SetIntersectSurface", "SetIntersectBBOX", "SetProximity", "SetLandmark", "NodeSetCanModify", "NodeSetOverwrite", "NodeSetLighting"], StringComparer.Ordinal);
    private static readonly HashSet<string> Disableable = new([.. Copyable, "AddChild", "DeleteChild", "NodeSetDescription"], StringComparer.Ordinal);
    private static bool Same(SourceInstruction? a, SourceInstruction b) => a != null && a.Line == b.Line && a.Script.Equals(b.Script, StringComparison.OrdinalIgnoreCase);
    private static SourceInstruction Created(SourceObjectTarget target, string[] commands, string verb)
    {
        var origin = target.Origin;
        var created = origin.Created ?? throw new InvalidDataException($"{target.Node.Name} was neither loaded from a model nor created by a script instruction.");
        if (!commands.Contains(created.Command)) throw new InvalidDataException($"{target.Node.Name} was made by {created.Command} ({created.Script} line {created.Line}); only objects a script loaded or created can be {verb} here.");
        if (target.Provenance.Values.Any(p => p.Database && Same(p.Load, created))) throw new InvalidDataException($"{target.Node.Name} is the load of the mission database; it cannot be {verb}.");
        return created;
    }
    private static WorldNode SingleParent(SourceObjectTarget target, WorldNode node, string verb)
    {
        if (node.Parents.Count == 0) throw new InvalidDataException($"{node.Name} is not placed in the world (a template that resources place copies of by name); it cannot be {verb} here.");
        if (node.Parents.Count > 1) throw new InvalidDataException($"{node.Name} has several parents; it cannot be {verb} here.");
        Unique(target, node.Parents[0].Name, $"{node.Name}'s parent");
        return node.Parents[0];
    }
    /// <summary>FindNode and AddChild find the newest node with a name; lines added at the end find the intended node only when its name is unique.</summary>
    private static void Unique(SourceObjectTarget target, string name, string what)
    {
        int count = target.World.Nodes.Count(n => n.Name == name);
        if (count != 1) throw new InvalidDataException($"{count} nodes are named {name}, so a script line cannot find {what} by name; rename them first.");
    }
    private static void CheckName(string name)
    {
        if (name.Length is 0 or > 32 || name.Any(c => c <= ' ' || c > '~' || c is '#' or '%' or ',' or ';' or '"'))
            throw new InvalidDataException("A node name has 1–32 printable characters without spaces, commas, quotes or # % ;.");
        // Importers drop an editor's ".001" suffix, which would give the copy the original's name.
        if (System.Text.RegularExpressions.Regex.IsMatch(name, @"\.\d{3}\z")) throw new InvalidDataException($"{name} ends like an editor's copy suffix (.001), which model imports drop; choose another name.");
    }
    /// <summary>A node and every node below it.</summary>
    private static List<WorldNode> Subtree(WorldNode node)
    {
        List<WorldNode> nodes = []; HashSet<WorldNode> seen = new(ReferenceEqualityComparer.Instance); Stack<WorldNode> pending = new([node]);
        while (pending.TryPop(out var n)) if (seen.Add(n)) { nodes.Add(n); foreach (var c in n.Children) pending.Push(c); }
        return nodes;
    }
    /// <summary>Refuses when a script instruction (other than <paramref name="handled"/>) acts on one of <paramref name="nodes"/> or finds it by name.</summary>
    private static void RefuseUsers(SourceObjectTarget target, IEnumerable<WorldNode> nodes, string name, string verb, IEnumerable<SourceInstruction>? handled = null)
    {
        var skip = (handled ?? []).ToList();
        foreach (var n in nodes)
            if (target.Provenance.TryGetValue(n, out var p) && p.Applied.Concat(p.Named).FirstOrDefault(u => !skip.Any(h => Same(h, u))) is { } user)
                throw new InvalidDataException($"{user.Script} line {user.Line} ({user.Command}) acts on {n.Name}{(ReferenceEquals(n, target.Node) ? "" : $", which is below {name}")}; {name} cannot be {verb} until that instruction changes.");
    }
    /// <summary>An argument as one script token, refused when the tokenizer would read it otherwise.</summary>
    private static string Token(string value) => GameGenScriptText.WriteLine(["X", value]) is not null && value.Length > 0 ? value : throw new InvalidDataException($"'{value}' cannot be written as one script token.");
    private static IEnumerable<WorldNode> Ancestors(WorldNode node)
    {
        HashSet<WorldNode> seen = new(ReferenceEqualityComparer.Instance); Stack<WorldNode> pending = new(node.Parents);
        while (pending.TryPop(out var parent)) if (seen.Add(parent)) { yield return parent; foreach (var up in parent.Parents) pending.Push(up); }
    }
    /// <summary>A node's world transform: its local transform under its first parent's, up to the root.</summary>
    private static Matrix4x4 WorldMatrix(WorldNode node)
    {
        var m = Matrix4x4.Identity; WorldNode? at = node;
        for (int depth = 0; at != null && depth <= GltfDocument.MaximumDepth; depth++, at = at.Parents.FirstOrDefault())
            if (at.Class == WorldNodeClass.Object3D) m *= WorldUpdate.LocalMatrix(at) ?? Matrix4x4.Identity;
        return m;
    }
    /// <summary>
    /// Whether a requested value differs from the one shown. Values echo exactly (Properties writes them round-trip, MCP as
    /// doubles), so only a couple of float steps count as the same.
    /// </summary>
    private static bool Differs(Vector3 requested, Vector3 shown) => Enumerable.Range(0, 3).Any(i => Differs(requested[i], shown[i]));
    private static bool Differs(float requested, float shown) => MathF.Abs(requested - shown) > 2.5e-7f * MathF.Max(1, MathF.Abs(shown));
    private static bool Near(Matrix4x4 a, Matrix4x4 b)
    {
        for (int i = 0; i < 4; i++) for (int j = 0; j < 4; j++) if (MathF.Abs(a[i, j] - b[i, j]) > 1e-5f) return false;
        return true;
    }

    /// <summary>A script number as the shipped scripts write them: shortest round-trip form with a decimal point.</summary>
    public static string Number(float value)
    {
        if (!float.IsFinite(value)) throw new InvalidDataException("Values must be finite.");
        string text = value.ToString("R", CultureInfo.InvariantCulture);
        return text.Contains('.') || text.Contains('E') ? text : text + ".0";
    }

    private static SourceEditPlan GltfEdit(SourceWorkspace workspace, WorldNodeProvenance origin, string label, Action<JsonObject> change, CancellationToken token) =>
        GltfFile(workspace, origin, label, (_, node) => change(node), token, []);
    /// <summary>A change of the glTF file a node came from: <paramref name="change"/> gets the document and the node.</summary>
    private static SourceEditPlan GltfFile(SourceWorkspace workspace, WorldNodeProvenance origin, string label, Action<JsonObject, JsonObject> change, CancellationToken token, IReadOnlyList<string> notes)
    {
        string file = origin.ModelFile!;
        byte[] bytes = workspace.Read(file, token) ?? throw new InvalidDataException($"{file} no longer exists.");
        if (!file.EndsWith(".gltf", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"{file} is a binary glTF; save it as .gltf to edit its nodes here.");
        JsonNode? root;
        try { root = JsonNode.Parse(bytes, documentOptions: new() { MaxDepth = 256 }); }
        catch (JsonException ex) { throw new InvalidDataException($"{file} is not valid JSON: {ex.Message}", ex); }
        if (root is not JsonObject document || document["nodes"] is not JsonArray nodes || origin.ModelNode < 0 || origin.ModelNode >= nodes.Count || nodes[origin.ModelNode] is not JsonObject node)
            throw new InvalidDataException($"{file} no longer has node {origin.ModelNode}; rebuild the world.");
        // The file may have changed since the build (another program, Blender): the node must still be the one built.
        if (origin.ModelNodeName != null && (node["name"] is JsonValue n && n.TryGetValue(out string? name) ? name : "") != origin.ModelNodeName)
            throw new InvalidDataException($"Node {origin.ModelNode} of {file} is no longer {origin.ModelNodeName}; reload the world before editing it.");
        change(document, node);
        // zStudio and Blender write glTF indented or minified; keep the file's style.
        bool indented = bytes.AsSpan(0, Math.Min(bytes.Length, 4096)).Contains((byte)'\n');
        byte[] content = Encoding.UTF8.GetBytes(root.ToJsonString(new JsonSerializerOptions { WriteIndented = indented }));
        if (content.AsSpan().SequenceEqual(bytes)) return new(label, [], $"{file} node {origin.ModelNode}", notes);
        List<string> all = [.. notes];
        if (!origin.Database) all.Add($"{file} is a model file: the change applies wherever it is loaded.");
        return new(label, [(file, content)], $"{file} node {origin.ModelNode}", all);
    }
    private static JsonArray Array(params float[] values) => new(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());
    private static void Check(Vector3 v)
    {
        if (!float.IsFinite(v.X) || !float.IsFinite(v.Y) || !float.IsFinite(v.Z) || Math.Abs(v.X) > SourceWorlds.MaximumCoordinate || Math.Abs(v.Y) > SourceWorlds.MaximumCoordinate || Math.Abs(v.Z) > SourceWorlds.MaximumCoordinate)
            throw new InvalidDataException($"Values must be finite and within ±{SourceWorlds.MaximumCoordinate:N0}.");
    }

    /// <summary>Token replacements and inserted lines of one script, applied together: replacements first, then insertions from the bottom up.</summary>
    private sealed class ScriptEdit(SourceWorkspace workspace, string script, IReadOnlyDictionary<(string Script, int Line), int> executions, CancellationToken token, string? mission = null)
    {
        private readonly Dictionary<int, Dictionary<int, string>> replacements = [];
        private readonly Dictionary<int, List<IReadOnlyList<string>>> insertions = [];
        private readonly Dictionary<int, IReadOnlyList<string>> lines = [];
        /// <summary>The tokens each line the edit touches had when the world was built; the script must still hold them.</summary>
        private readonly Dictionary<int, IReadOnlyList<string>> expected = [];
        private bool sharedChecked;
        public List<string> Notes { get; } = [];

        private void Editable(SourceInstruction instruction)
        {
            if (!instruction.Script.Equals(script, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The instructions of one edit must be in one script.");
            int runs = executions.GetValueOrDefault((instruction.Script, instruction.Line));
            if (runs > 1) throw new InvalidDataException($"{instruction.Script} line {instruction.Line} runs {runs} times while the world is built, so it sets this value for more than one object; edit the script directly.");
            expected[instruction.Line] = instruction.Tokens;
        }
        /// <summary>Refuses a script another mission's world script runs too (lines added before the world is written included).</summary>
        private void CheckShared()
        {
            if (mission == null || sharedChecked) return;
            sharedChecked = true;
            var others = MissionsRunning(workspace, script, token).Where(m => !m.Equals(mission, StringComparison.OrdinalIgnoreCase)).Take(4).ToArray();
            if (others.Length > 0) throw new InvalidDataException($"{script} also runs in {string.Join(", ", others)}; editing it would change those missions too. Edit it in the script directly.");
        }
        public void Replace(SourceInstruction instruction, Dictionary<int, string> values)
        {
            Editable(instruction);
            foreach (var index in values.Keys)
                if (index < instruction.Tokens.Count && instruction.Tokens[index].Contains('%')) throw new InvalidDataException($"{instruction.Script} line {instruction.Line} takes this value from a macro ({instruction.Tokens[index]}); edit the macro instead.");
            if (!replacements.TryGetValue(instruction.Line, out var existing)) replacements[instruction.Line] = existing = [];
            foreach (var (index, value) in values) existing[index] = value;
        }
        public void ReplaceLine(SourceInstruction instruction, IReadOnlyList<string> tokens)
        {
            Editable(instruction);
            if (instruction.Tokens.Skip(1).Any(t => t.Contains('%'))) throw new InvalidDataException($"{instruction.Script} line {instruction.Line} uses macros; edit it in the script directly.");
            lines[instruction.Line] = tokens;
        }
        private readonly HashSet<int> comments = [];
        private readonly List<IReadOnlyList<string>> beforeWrite = [];
        /// <summary>Turns an instruction into a comment.</summary>
        public void Comment(SourceInstruction instruction) { Editable(instruction); comments.Add(instruction.Line); }
        /// <summary>Adds lines right before the instruction that wrote the world (<paramref name="write"/>, when the build recorded it).</summary>
        public void InsertBeforeWrite(IEnumerable<IReadOnlyList<string>> tokens, SourceInstruction? write = null)
        {
            beforeWrite.AddRange(tokens);
            if (write == null) return;
            if (!write.Script.Equals(script, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"The world is written by {write.Script}, not {script}; add the lines there directly.");
            writeLine = write.Line; expected[write.Line] = write.Tokens;
        }
        private int? writeLine;
        public void Insert(SourceInstruction after, IReadOnlyList<string> tokens)
        {
            Editable(after);
            if (!insertions.TryGetValue(after.Line + 1, out var list)) insertions[after.Line + 1] = list = [];
            list.Add(tokens);
        }
        /// <summary>A vector instruction: the existing one changes; a missing one is added when the value differs from the default.</summary>
        /// <remarks>
        /// A component changes when it differs from <paramref name="shown"/> (the value the user saw), or, without it, from
        /// the value the build used by more than a decomposition's noise. Other components keep their tokens (a macro too).
        /// </remarks>
        public void Set(SourceInstruction? writer, string command, Vector3 value, Vector3 unset, SourceInstruction anchor, Vector3? shown = null)
        {
            // The values the build used: a missing argument reads as 0, as the interpreter reads it; no instruction, the default.
            Vector3 built = writer == null ? unset : new(Arg(0), Arg(1), Arg(2));
            float Arg(int i) => i < writer!.Args.Count ? WorldAssembler.Number(writer.Args[i]) : 0;
            bool[] changed = [.. Enumerable.Range(0, 3).Select(i => shown is { } s ? Differs(value[i], s[i])
                : MathF.Abs(value[i] - built[i]) > 1e-5f * MathF.Max(1, MathF.Abs(value[i])))];
            if (!changed.Any(c => c)) return;
            string Component(int i) => Number(changed[i] ? value[i] : built[i]);
            if (writer == null)
            {
                var whole = new Vector3(changed[0] ? value.X : built.X, changed[1] ? value.Y : built.Y, changed[2] ? value.Z : built.Z);
                if (whole != unset) Insert(anchor, [command, Component(0), Component(1), Component(2)]);
                return;
            }
            // A writer missing values is written whole.
            if (writer.Args.Count < 3) { ReplaceLine(writer, [command, Component(0), Component(1), Component(2)]); return; }
            Replace(writer, Enumerable.Range(0, 3).Where(i => changed[i]).ToDictionary(i => i + 1, i => Number(value[i])));
        }
        public IReadOnlyList<(string Relative, byte[] Content)> Changes()
        {
            string relative = script;
            var syntax = GameGenScriptSyntax.Parse(workspace.Read(relative, token) ?? throw new InvalidDataException($"{relative} no longer exists."));
            // Line numbers come from the build; the lines must still be the instructions it ran.
            foreach (var (line, tokens) in expected)
                if (line > syntax.Lines.Count || !syntax.Line(line).Tokens.SequenceEqual(tokens, StringComparer.Ordinal))
                    throw new InvalidDataException($"{relative} line {line} changed since the world was built; reload the world first.");
            foreach (var (line, values) in replacements)
            {
                var current = syntax.Line(line);
                if (!current.IsInstruction) throw new InvalidDataException($"{relative} line {line} changed since the world was built; rebuild it first.");
                syntax = GameGenScriptSyntax.Parse(syntax.ReplaceTokens(line, values));
            }
            foreach (int line in comments)
            {
                if (!syntax.Line(line).IsInstruction) throw new InvalidDataException($"{relative} line {line} changed since the world was built; rebuild it first.");
                syntax = GameGenScriptSyntax.Parse(syntax.CommentOut(line));
            }
            if (beforeWrite.Count > 0)
            {
                int write = writeLine ?? syntax.Lines.FirstOrDefault(l => l.IsInstruction && l.Tokens[0] == "GameZWriteZBDFile")?.Number
                    ?? throw new InvalidDataException($"{relative} does not write the world itself (GameZWriteZBDFile); add the lines to the script that does.");
                if (!insertions.TryGetValue(write, out var list)) insertions[write] = list = [];
                list.AddRange(beforeWrite);
            }
            // Line-changing steps run from the bottom up, so the line numbers of those still to come stay valid.
            // At one line, a rewrite (which inserts after it) runs before lines inserted before it, which would shift it.
            var steps = insertions.Select(i => (Line: i.Key, Rewrite: false)).Concat(lines.Keys.Select(l => (Line: l, Rewrite: true))).OrderByDescending(s => s.Line).ThenByDescending(s => s.Rewrite);
            foreach (var (line, rewrite) in steps)
            {
                if (!rewrite) { syntax = GameGenScriptSyntax.Parse(syntax.InsertLines(Math.Min(line, syntax.Lines.Count + 1), insertions[line])); continue; }
                // A whole instruction rewritten: the old one becomes a comment and the new one follows it, keeping both readable.
                syntax = GameGenScriptSyntax.Parse(syntax.InsertLines(line + 1, [lines[line]]));
                syntax = GameGenScriptSyntax.Parse(syntax.CommentOut(line));
            }
            if (replacements.Count == 0 && lines.Count == 0 && insertions.Count == 0 && comments.Count == 0 && beforeWrite.Count == 0) return [];
            CheckShared();
            return [(relative, syntax.Encode())];
        }
    }

    /// <summary>
    /// The missions whose world scripts (gamegen/mN.gs) run <paramref name="script"/>, themselves or through the scripts they
    /// source (every source line counts, whatever condition guards it).
    /// </summary>
    internal static IReadOnlyList<string> MissionsRunning(SourceWorkspace workspace, string script, CancellationToken token)
    {
        List<string> missions = [];
        var worlds = SourceProject.Files(workspace.Root, SourceProject.GameGenFolder, n => System.Text.RegularExpressions.Regex.IsMatch(n, @"\Am\d+\.gs\z", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            .Where(p => p.Count(c => c == '/') == 1);
        foreach (string world in worlds)
        {
            HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase); Stack<string> pending = new([world]);
            while (pending.TryPop(out var file))
            {
                token.ThrowIfCancellationRequested();
                if (!seen.Add(file) || seen.Count > 512) continue;
                if (file.Equals(script, StringComparison.OrdinalIgnoreCase)) { missions.Add(Path.GetFileNameWithoutExtension(world).ToLowerInvariant()); break; }
                byte[]? bytes;
                try { bytes = workspace.Read(file, token); }
                catch (InvalidDataException) { continue; }
                if (bytes == null) continue;
                foreach (var line in GameGenScriptSyntax.Parse(bytes).Lines)
                    if (line.IsInstruction && line.Tokens.Count > 1 && ScriptConditions.IsSource(line.Tokens[0]) && !line.Tokens[1].Contains('%'))
                        pending.Push($"{SourceProject.GameGenFolder}/{line.Tokens[1].Replace('\\', '/')}");
            }
        }
        return missions;
    }
}
