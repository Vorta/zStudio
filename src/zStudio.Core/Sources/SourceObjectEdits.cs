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
                for (int j = 0; j < 3 && same; j++) same = MathF.Abs(composed[i, j] - m[i, j]) <= 1e-4f * length + 1e-6f;
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
    internal static Vector3 Snap(Vector3 v, bool angles) => new(SnapOne(v.X, angles, angles), SnapOne(v.Y, angles, angles), SnapOne(v.Z, angles, angles));
    /// <summary>A position's noise removed as an angle's (5e-6 becomes 0), but no half turn: −200 stays −200.</summary>
    internal static Vector3 SnapPosition(Vector3 v) => new(SnapOne(v.X, true, false), SnapOne(v.Y, true, false), SnapOne(v.Z, true, false));
    private static float SnapOne(float x, bool tiny, bool turn)
    {
        if (tiny && MathF.Abs(x) < 1e-4f) return 0;
        float rounded = MathF.Round(x, 3);
        float snapped = MathF.Abs(x) >= 1e-3f && MathF.Abs(x - rounded) <= 1e-6f * MathF.Max(1, MathF.Abs(x)) ? rounded : x;
        // A half turn reads as 180, not −180 (atan2's −π).
        return turn && snapped <= -180f ? snapped + 360f : snapped;
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
        // Rows of Rz·Rx·Ry (see Matrix): the Z axis gives pitch (atan2 keeps it precise near ±90°, where asin loses digits)
        // and yaw. Roll is fitted to them: R·(Rx·Ry)ᵀ is Rz, read from the whole rotation, so the noise of the
        // cos(pitch)-scaled elements near ±90° does not build up over edits. At ±90° yaw and roll turn about one axis and the
        // Z axis's horizontal part is float noise (up to 5e-7 through a glTF quaternion), which would pick any yaw, so an
        // edit of the scale could show another pair for the same turn: there yaw is 0, pitch is read in the YZ plane (beyond
        // ±90° when the noise points back) and roll takes the rest, dropping a tilt of at most |z.X| (6e-7 rad, 3.4e-5°).
        float horizontal = MathF.Sqrt(z.X * z.X + z.Z * z.Z);
        bool vertical = horizontal < 6e-7f;
        float rx = vertical ? MathF.Atan2(-z.Y, z.Z) : MathF.Atan2(-z.Y, horizontal), ry = vertical ? 0 : MathF.Atan2(z.X, z.Z);
        var rotation = new Matrix4x4(x.X, x.Y, x.Z, 0, y.X, y.Y, y.Z, 0, z.X, z.Y, z.Z, 0, 0, 0, 0, 1);
        var roll = rotation * Matrix4x4.Transpose(Matrix4x4.CreateRotationX(rx) * Matrix4x4.CreateRotationY(ry));
        float rz = MathF.Atan2(roll.M12, roll.M11);
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
    public static SourceEditPlan PlanTransform(SourceWorkspace workspace, string nodeName, WorldNodeProvenance origin, IReadOnlyDictionary<(string Script, int Line), int> executions, ObjectTransform requested, CancellationToken token = default, string? mission = null, ObjectTransform? current = null,
        IEnumerable<WorldNodeProvenance>? copies = null)
    {
        Generated(origin, nodeName);
        string label = $"Move {nodeName}";
        bool position = current is not { } c || Differs(requested.Position, c.Position);
        bool rotation = current is not { } r || Differs(requested.RotationDegrees, r.RotationDegrees);
        bool scale = current is not { } s || Differs(requested.Scale, s.Scale);
        // Only what changes: a node built at the limit (a scale reading back as 1000000.06) can still move.
        if (position) Check(requested.Position);
        if (rotation) Check(requested.RotationDegrees);
        if (scale) { Check(requested.Scale); CheckScale(requested.Scale); }
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
        // A node of a glTF file: its transform is the node's, except a translation a script sets. The file's node changes in
        // every copy of a part and every load of a model file, so a script transform of another one would start or stop
        // applying there.
        if ((copies ?? []).Where(c => !ReferenceEquals(c, origin)).SelectMany(c => TransformCommands.Where(c.Writers.ContainsKey).Select(k => c.Writers[k])).FirstOrDefault() is { } other)
            throw new InvalidDataException($"{other.Script} line {other.Line} ({other.Command}) sets the transform of another {(origin.Database ? "copy" : "load")} of {nodeName} in {origin.ModelFile}; edit those instructions in the scripts first.");
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
                // A matrix with a shear (a move under a parent whose scale is not uniform writes one) shows decomposed; rebuilt
                // from the shown rotation and scale it would change shape.
                if ((rotation || scale) && HasShear(local))
                    throw new InvalidDataException($"{nodeName}'s transform has a shear or a zero scale its rotation and scale cannot show; change them in Blender, or move it alone.");
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
    /// <remarks>
    /// <paramref name="copies"/> (<see cref="CopiesOf"/>) are the nodes a glTF node's edit reaches; with <paramref name="world"/>
    /// and <paramref name="write"/>, a value nothing set yet goes to this mission's world script when the script that created
    /// the node also runs in other missions (see <see cref="PlanCommand"/>).
    /// </remarks>
    public static SourceEditPlan PlanFlag(SourceWorkspace workspace, string nodeName, WorldNodeProvenance origin, IReadOnlyDictionary<(string Script, int Line), int> executions, uint bit, bool on, CancellationToken token = default, string? mission = null,
        IEnumerable<WorldNodeProvenance>? copies = null, GameZWorld? world = null, SourceInstruction? write = null)
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
        {
            // The file's node changes in every copy and load of it; one whose script sets the flag keeps the value it sets.
            List<string> kept = [.. (copies ?? []).Where(c => !ReferenceEquals(c, origin)).Select(c => command != null && c.Writers.TryGetValue(command, out var w) ? w : null).OfType<SourceInstruction>()
                .Take(1).Select(w => $"{w.Script} line {w.Line} ({w.Command}) sets this flag on another {(origin.Database ? "copy" : "load")} of {nodeName}, which keeps the value it sets.")];
            return GltfEdit(workspace, origin, label, node =>
            {
                var extras = node["extras"] as JsonObject ?? (JsonObject)(node["extras"] = new JsonObject());
                var recoil = extras[WorldGltf.Key] as JsonObject ?? (JsonObject)(extras[WorldGltf.Key] = new JsonObject());
                // As the importer reads it: hexadecimal, with or without 0x.
                uint carried = recoil["flags"] is JsonValue text && text.TryGetValue<string>(out var hex)
                    && uint.TryParse(hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? hex.AsSpan(2) : hex.AsSpan(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint parsed) ? parsed & WorldGltf.CarriedFlags : WorldGltf.DefaultCarried;
                carried = on ? carried | bit : carried & ~bit;
                if (carried == WorldGltf.DefaultCarried) recoil.Remove("flags"); else recoil["flags"] = $"0x{carried:X8}";
            }, token, kept);
        }
        if (command == null) throw new InvalidDataException($"No script command sets flag 0x{bit:X}; {nodeName} was created by a script.");
        var anchor = origin.Created ?? throw new InvalidDataException($"{nodeName} was neither loaded from a model nor created by a script instruction.");
        return Insert(workspace, nodeName, anchor, [command, value], label, executions, token, mission, world, write);
    }
    /// <summary>Whether a source can set flag <paramref name="bit"/> of the node <paramref name="origin"/> describes: a glTF node carries each, a script sets those a command sets.</summary>
    public static bool FlagSettable(WorldNodeProvenance origin, uint bit) =>
        origin.Terrain == null && System.Numerics.BitOperations.PopCount(bit) == 1 && (bit & WorldGltf.CarriedFlags) != 0 && (origin.ModelFile != null || FlagCommands.ContainsKey(bit));

    /// <summary>
    /// A plan that sets the arguments of one property command (WorldSetFogColor, LightSetDiffuse, …) of a node a script made:
    /// the instruction that last set it changes, or a new one follows the instruction that created the node. When another
    /// mission's world script also runs that script (world.gw's NewWorld), the new line goes before this mission's world is
    /// written instead, after a FindNode of the node (whose name must be unique in <paramref name="world"/>): nothing set the
    /// value before, so setting it last is the same.
    /// </summary>
    public static SourceEditPlan PlanCommand(SourceWorkspace workspace, string nodeName, WorldNodeProvenance origin, IReadOnlyDictionary<(string Script, int Line), int> executions, string command, IReadOnlyList<string> args, CancellationToken token = default, string? mission = null,
        GameZWorld? world = null, SourceInstruction? write = null)
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
        return Insert(workspace, nodeName, anchor, [command, .. args], label, executions, token, mission, world, write);
    }
    /// <summary>
    /// Adds an instruction right after the one that created a node; when other missions run that script too, before this
    /// mission's world is written instead, after a FindNode of the node (see <see cref="PlanCommand"/>).
    /// </summary>
    private static SourceEditPlan Insert(SourceWorkspace workspace, string nodeName, SourceInstruction anchor, IReadOnlyList<string> tokens, string label, IReadOnlyDictionary<(string Script, int Line), int> executions, CancellationToken token, string? mission, GameZWorld? world, SourceInstruction? write)
    {
        if (mission != null && world != null && nodeName.Length > 0 && world.Nodes.Count(n => n.Name == nodeName) == 1
            && MissionsRunning(workspace, anchor.Script, token).Any(m => !m.Equals(mission, StringComparison.OrdinalIgnoreCase)))
        {
            string script = SourceBuilder.WorldScript(mission);
            ScriptEdit end = new(workspace, script, executions, token, mission);
            end.InsertBeforeWrite([["FindNode", Token(nodeName)], tokens], write);
            return new(label, end.Changes(), $"{script}, before the world is written", [$"{anchor.Script} also runs in other missions, so the line goes before {script} writes the world."]);
        }
        ScriptEdit insert = new(workspace, anchor.Script, executions, token, mission);
        insert.Insert(anchor, tokens);
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
    /// A plan that deletes an object: a node of the mission database leaves its glTF file with its descendants (from every
    /// copy of an instance holding it); an object a script created has every instruction that created, changed or attached
    /// it or one of the parts its load made turned into a comment, so the rest of the script runs as before. Objects other
    /// instructions use (a camera's horizon, a world's light) are refused.
    /// </summary>
    public static SourceEditPlan PlanDelete(SourceObjectTarget target, CancellationToken token = default)
    {
        var node = target.Node; var origin = target.Origin;
        Generated(origin, node.Name);
        string label = $"Delete {node.Name}";
        if (node.Class is not (WorldNodeClass.Object3D or WorldNodeClass.Lod)) throw new InvalidDataException($"{node.Name} is a {node.Class} node; only objects can be deleted here.");
        List<string> notes = [$"Animations and resources that find {node.Name} by name no longer find it; Problems lists what the rebuild reports."];
        if (origin.ModelFile != null)
        {
            if (!origin.Database) throw new InvalidDataException($"{node.Name} is part of the model file {origin.ModelFile}; delete the object that loads it, or remove the part in Blender.");
            // A script instruction that acts on the node or a part of it would act on another node, or none, once it is gone
            // (also one attaching it to a second parent). A node the file shares leaves every parent with its copies.
            RefuseUsers(target, FileSubtree(target, node, origin, token), node.Name, "deleted");
            return GltfFile(target.Workspace, origin, label, (root, copies) => GltfNodeEdits.Remove(root, copies), token, notes);
        }
        if (node.Parents.Count > 1) throw new InvalidDataException($"{node.Name} has several parents (a shared node); delete it in its source directly.");
        var created = Created(target, ["LoadGameGen", "NewObject3D"], "deleted");
        // The instructions on the object's own nodes (the node and every node its load made, wherever a script put them)
        // become comments; other objects attached below it refuse while instructions act on them.
        var own = Own(target, node, created);
        HashSet<WorldNode> owned = new(own, ReferenceEqualityComparer.Instance);
        List<SourceInstruction> instructions = [created, .. own.SelectMany(n => target.Provenance.TryGetValue(n, out var p) ? p.Applied.Concat(p.Named) : Enumerable.Empty<SourceInstruction>())];
        RefuseUsers(target, Subtree(node).Where(n => !owned.Contains(n)), node.Name, "deleted", instructions);
        // FindSubNode selects a node below the current one, which the lines after it act on: taken out with them, it can only
        // have found one of the object's own nodes when nothing was ever attached below the object.
        bool attaches = own.Any(n => target.Provenance.TryGetValue(n, out var p) && p.Applied.Any(i => i.Command == "AddChild"));
        foreach (var part in own)
        {
            if (!target.Provenance.TryGetValue(part, out var p)) continue;
            if (p.Named.FirstOrDefault(n => n.Command is not ("AddChild" or "DeleteChild" or "DeleteTree")) is { } user)
                throw new InvalidDataException($"{user.Script} line {user.Line} ({user.Command}) uses {part.Name}{(ReferenceEquals(part, node) ? "" : $", a part of {node.Name}")}; change that instruction first.");
            foreach (var instruction in p.Applied)
            {
                if (!Disableable.Contains(instruction.Command) && !(instruction.Command == "FindSubNode" && !attaches))
                    throw new InvalidDataException($"{instruction.Script} line {instruction.Line} ({instruction.Command}) acts on {part.Name}{(ReferenceEquals(part, node) ? "" : $", a part of {node.Name},")} in a way a deletion cannot take out; edit the script directly.");
                if (instruction.Command == "AddChild" && instruction.Args.Count > 0) notes.Add($"{instruction.Args[0]} was attached to {part.Name} and is no longer in the world.");
                if (instruction.Command == "NodeSetDescription" && instruction.Args.Count > 0 && !ReferenceEquals(part, node)) notes.Add($"Animations and resources that find {instruction.Args[0]} by name no longer find it.");
            }
        }
        ScriptEdits edits = new(target, token);
        foreach (var instruction in instructions) edits[instruction.Script].Comment(instruction);
        return new(label, edits.Changes(), $"{created.Script} line {created.Line}", notes);
    }
    /// <summary>An object's own nodes: the node, and for the root a LoadGameGen made, every node of that load, wherever a script put it.</summary>
    private static List<WorldNode> Own(SourceObjectTarget target, WorldNode node, SourceInstruction created) =>
        [node, .. created.Command == "LoadGameGen" ? target.Provenance.Where(p => p.Value.Load is { } load && Same(load, created) && !ReferenceEquals(p.Key, node)).Select(p => p.Key) : []];

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
        // Only the values that differ from the original's shown transform (as PlanTransform): a copy of a node built at the
        // limit (a scale reading back as 1000000.06) can still take a new rotation.
        if (transform is { } requested)
        {
            var shown = ObjectTransform.Of(node);
            if (Differs(requested.Position, shown.Position)) Check(requested.Position);
            if (!keepBasis && Differs(requested.RotationDegrees, shown.RotationDegrees)) Check(requested.RotationDegrees);
            if (!keepBasis && Differs(requested.Scale, shown.Scale)) { Check(requested.Scale); CheckScale(requested.Scale); }
        }
        string label = $"Copy {node.Name} as {name}";
        string parts = $"The copy's parts keep the original's part names; an animation that finds a part by name may find the copy's.";
        if (origin.ModelFile != null)
        {
            if (!origin.Database) throw new InvalidDataException($"{node.Name} is part of the model file {origin.ModelFile}; copy the object that loads it.");
            // The copy stands where the file puts the original (beside it in its file parent): one a script attached or
            // detached elsewhere would land where the script took the original from (morfUtil.gw's morph objects).
            if (origin.Named.FirstOrDefault(i => i.Command is "AddChild" or "DeleteChild") is { } placing)
                throw new InvalidDataException($"{placing.Script} line {placing.Line} ({placing.Command}) places {node.Name} elsewhere than its file does, so a copy would not stand beside it; copy it in the scripts directly.");
            // The copy's parts are newer and keep their names: a script finding one by name would find the copy's.
            RefuseUsers(target, FileSubtree(target, node, origin, token).Where(n => !ReferenceEquals(n, node)), node.Name, "copied");
            // A part a script takes out of the node (from its file or a file it references) would be copied too, and the
            // script's later lookups would find the copy's.
            if (origin.Applied.FirstOrDefault(i => i.Command == "DeleteChild") is { } detach)
                throw new InvalidDataException($"{detach.Script} line {detach.Line} (DeleteChild) takes a part out of {node.Name}; {node.Name} cannot be copied until that changes.");
            // The copy takes this copy's built place and flags into every copy of a part, so script values on any one copy would
            // reach the others' copies, or miss its own.
            var partCopies = WithCopies(target, [node]).ToList();
            if (partCopies.Count > 1)
                foreach (var each in partCopies)
                    if (target.Provenance.TryGetValue(each, out var p) && p.Applied.Concat(p.Named).FirstOrDefault() is { } user)
                        throw new InvalidDataException($"{user.Script} line {user.Line} ({user.Command}) acts on {(ReferenceEquals(each, node) ? "one" : "another")} copy of {node.Name} in {p.ModelFile}; {node.Name} cannot be copied until that changes.");
            // Script instructions find the original by name, so the copy gets what they set from the build instead: its
            // place (as an authored matrix) and its flags. Other instructions do not reach it.
            var built = WorldUpdate.LocalMatrix(node) ?? Matrix4x4.Identity;
            // A copy given its own rotation and scale is made of them alone; an original holding a shear would change shape.
            if (transform != null && !keepBasis && HasShear(built))
                throw new InvalidDataException($"{node.Name}'s transform has a shear or a zero scale its rotation and scale cannot show; copy it in place, or with a new position only.");
            Matrix4x4? local = transform is { } t ? keepBasis ? built with { Translation = t.Position } : t.Matrix()
                : TransformCommands.Any(origin.Writers.ContainsKey) ? built : null;
            bool flags = origin.Applied.Any(i => FlagCommands.Values.Contains(i.Command));
            List<string> copyNotes = ["The copy shares the original's meshes and textures (a point-only model, such as a lens flare, gets its own).", parts];
            // A part is copied wherever the database references it, and so is a copy made in it.
            int copies = target.Provenance.Values.Count(p => p.Part && p.ModelNode == origin.ModelNode && string.Equals(p.ModelFile, origin.ModelFile, StringComparison.OrdinalIgnoreCase));
            if (origin.Part && copies > 1) copyNotes.Add($"The mission database copies {origin.ModelFile} {copies} times, so the world gets {copies} nodes named {name}.");
            foreach (var i in origin.Applied.Where(i => !TransformCommands.Contains(i.Command) && !FlagCommands.Values.Contains(i.Command) && i.Command is not ("NodeSetLighting" or "FindSubNode")))
                copyNotes.Add($"{i.Script} line {i.Line} ({i.Command}) acts on {node.Name} by name; the copy does not get it.");
            return GltfFile(target.Workspace, origin, label, (root, copies) =>
            {
                // Beside the node in every copy of an instance holding it, as copies of one new instance where it is one.
                Dictionary<long, long> instances = [];
                foreach (int each in copies)
                {
                    int copy = GltfNodeEdits.Duplicate(root, each, name, instances);
                    var copied = (JsonObject)root["nodes"]![copy]!;
                    if (local is { } m) GltfNodeEdits.SetLocal(copied, m);
                    if (flags) ((JsonObject)copied["extras"]![WorldGltf.Key]!)["flags"] = $"0x{node.Flags & WorldGltf.CarriedFlags:X8}";
                }
            }, token, copyNotes);
        }
        var created = Created(target, ["LoadGameGen"], "copied");
        string file = origin.LoadedFile ?? throw new InvalidDataException($"{node.Name}'s LoadGameGen found no model file.");
        // The copy repeats the instructions on the object itself; those that change its parts (also parts a script put
        // elsewhere) would not be repeated.
        RefuseUsers(target, Own(target, node, created).Concat(Subtree(node)).Where(n => !ReferenceEquals(n, node)), node.Name, "copied");
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
        if (parent != null && target.Provenance.TryGetValue(parent, out var parentOrigin) && parentOrigin.Terrain is { } pieceRecipe)
            throw new InvalidDataException($"{parent.Name} is a terrain piece of {pieceRecipe}; its name and place change whenever the recipe does, so objects cannot move under it.");
        if (node.Parents.Count == 0) throw new InvalidDataException($"{node.Name} is not placed in the world (a template, or a node a script took out of its parent); move it in the scripts directly.");
        // A node several parents share (an instance, or a node scripts attached twice) is placed under each: moving it, or a
        // node inside it, would keep one place, and moving a node into it would place that node under each. For a node of a
        // glTF file only sharing within that file counts: a part referenced from inside a shared node is one copy in its own
        // file, and a move within the file keeps each place.
        bool InFile(WorldNode n) => target.Provenance.TryGetValue(n, out var p) && string.Equals(p.ModelFile, origin.ModelFile, StringComparison.OrdinalIgnoreCase);
        bool Shared(WorldNode n) => n.Parents.Count > 1 && (origin.ModelFile == null || InFile(n));
        if (node.Parents.Count > 1 || Ancestors(node).Any(Shared))
            throw new InvalidDataException($"{node.Name} is {(node.Parents.Count > 1 ? "" : "inside ")}a node several parents share; move it in its source directly.");
        if (parent != null && parent.Class != WorldNodeClass.World && !Ancestors(parent).Any(a => a.Class == WorldNodeClass.World))
            throw new InvalidDataException($"{parent.Name} is not placed in the world (a script took it out), so {node.Name} would leave the world under it; choose another parent.");
        if (parent != null && (parent.Parents.Count > 1 || Ancestors(parent).Any(Shared)))
            throw new InvalidDataException($"{parent.Name} is {(parent.Parents.Count > 1 ? "" : "inside ")}a node several parents share, so {node.Name} would be placed under each; choose another parent.");
        // Already under the world (a member of a group the build deletes): moving it there would only reorder the file.
        if ((parent == null || parent.Class == WorldNodeClass.World) && node.Parents.Any(p => p.Class == WorldNodeClass.World))
            throw new InvalidDataException($"{node.Name} is already a root of the world.");
        // A part's node moves to the top of its part (the part's references hold it), not into the world itself.
        bool top = parent == null || parent.Class == WorldNodeClass.World;
        string label = top
            ? origin.ModelFile != null && origin.Part ? $"Move {node.Name} to the top of {Path.GetFileName(origin.ModelFile)}" : $"Move {node.Name} to the world"
            : $"Move {node.Name} under {parent!.Name}";
        // A level-of-detail node draws what is below it only within its distance range.
        List<WorldNode> lods = [.. Ancestors(node).Where(a => a.Class == WorldNodeClass.Lod)];
        List<WorldNode> lodsAfter = [.. (top ? origin.ModelFile != null ? Ancestors(node).Where(a => !InFile(a)) : [] : Ancestors(parent!).Prepend(parent!)).Where(a => a.Class == WorldNodeClass.Lod)];
        List<string> notes = [.. lodsAfter.Where(l => !lods.Contains(l)).Select(l => $"{node.Name} now draws only within the distance range of {l.Name}, a level-of-detail node."),
            .. lods.Where(l => !lodsAfter.Contains(l)).Select(l => $"{node.Name} leaves {l.Name}, a level-of-detail node: it no longer depends on that node's distance range.")];
        if (origin.ModelFile != null)
        {
            if (!origin.Database) throw new InvalidDataException($"{node.Name} is part of the model file {origin.ModelFile}; move the object that loads it.");
            // The zone it keeps when the new place would give another (see GltfNodeEdits.Reparent): unknown when a part's copies
            // take different zones from their references, which one file cannot keep for each.
            uint? zone = origin.Part && WithCopies(target, [node]).Select(n => n.Zone & 0xFF).Distinct().Skip(1).Any() ? null : node.Zone & 0xFF;
            int? into = null;
            if (parent != null && parent.Class != WorldNodeClass.World)
            {
                if (!target.Provenance.TryGetValue(parent, out var p) || !p.Database || !string.Equals(p.ModelFile, origin.ModelFile, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException(origin.Part
                        ? $"{parent.Name} is not a node of {origin.ModelFile}; a node of a part of the mission database can only move under another node of that part, or to its top."
                        : $"{parent.Name} is not a node of {origin.ModelFile}; a mission database node can only move under another node of the database, or to the world.");
                into = p.ModelNode;
            }
            // The glTF keeps the node's place from the file's transforms; a script transform along either chain moves it
            // elsewhere, and a script that also attaches a node of either chain places an instance the glTF does not.
            // Every copy of a part moves: each copy's chain counts.
            var chain = WithCopies(target, [node]).Concat(parent == null ? [] : WithCopies(target, [parent])).SelectMany(n => Ancestors(n).Prepend(n))
                .Distinct(ReferenceEqualityComparer.Instance).Cast<WorldNode>().ToList();
            foreach (var n in chain)
                if (target.Provenance.TryGetValue(n, out var linked) && linked.Named.FirstOrDefault(x => x.Command == "AddChild") is { } attach)
                    throw new InvalidDataException($"{attach.Script} line {attach.Line} also attaches {n.Name} elsewhere; a new place in the glTF would not hold for that instance. Move it in the scripts directly.");
            if (chain.FirstOrDefault(n => target.Provenance.TryGetValue(n, out var p) && TransformCommands.Any(p.Writers.ContainsKey)) is { } scripted)
                throw new InvalidDataException($"A script sets {scripted.Name}'s transform, so the glTF alone cannot keep {node.Name} in place; move it in the scripts directly.");
            // NodeSetLighting lights every model below its node as the build has it then: the nodes of the file above the node
            // must stay those it runs on (outside the file, the chain stays as it is).
            var lit = Lighting(target, WithCopies(target, [node]).SelectMany(Ancestors).Where(InFile));
            var litAfter = Lighting(target, top ? [] : WithCopies(target, [parent!]).SelectMany(n => Ancestors(n).Prepend(n)).Where(InFile));
            if (lit.Keys.Concat(litAfter.Keys).FirstOrDefault(k => !lit.ContainsKey(k) || !litAfter.ContainsKey(k)) is { Script: not null } changed)
            {
                var (instruction, on) = lit.TryGetValue(changed, out var before) ? before : litAfter[changed];
                throw new InvalidDataException($"{instruction.Script} line {instruction.Line} (NodeSetLighting) lights everything below {on.Name}, so {node.Name} would be lit differently {(lit.ContainsKey(changed) ? $"away from {on.Name}" : $"under {on.Name}")}; move it in the scripts directly.");
            }
            bool topReached = !origin.Part || OutsideFile(node, InFile)?.Class == WorldNodeClass.World;
            var plan = GltfFile(target.Workspace, origin, label, (root, _) =>
            {
                // Moved into an instance (whose other holders a script may have taken out of the world, so that it does not
                // show as shared there), it would stand in the first copy only.
                if (into is int i && GltfNodeEdits.InstanceCopies(root, i).Count > 1)
                    throw new InvalidDataException($"{parent!.Name} is {(Marked((JsonObject)root["nodes"]![i]!) ? "" : "inside ")}a node {origin.ModelFile} places under several parents (an instance), so {node.Name} would stand in one copy only; choose another parent.");
                // A level-of-detail node has no transform of its own (the importer reads none): it stands where its parent
                // is. Compared in the file, which places every copy of a part alike (a part's top is where its references are).
                if (node.Class == WorldNodeClass.Lod && !SamePlace(GltfNodeEdits.Parent(root, origin.ModelNode) is int from ? GltfNodeEdits.World(root, from) : Matrix4x4.Identity, into is int to ? GltfNodeEdits.World(root, to) : Matrix4x4.Identity))
                    throw new InvalidDataException($"{node.Name} is a level-of-detail node, which stands where its parent is; it cannot keep its place {(parent != null ? $"under {parent.Name}" : origin.Part ? $"at the top of {Path.GetFileName(origin.ModelFile)}" : "under the world")}, which is elsewhere.");
                GltfNodeEdits.Reparent(root, origin.ModelNode, into, zone);
                // A group the build reaches from the file's top (through groups) is deleted with the database, its objects
                // joining the world: the build refuses one with geometry or a transform.
                if (GroupsReached(root, origin.ModelNode, topReached).Where(g => !EmptyGroup((JsonObject)root["nodes"]![g]!)).Select(g => (int?)g).FirstOrDefault() is int full)
                    throw new InvalidDataException($"{(full == origin.ModelNode ? node.Name : $"Group {((JsonObject)root["nodes"]![full]!)["name"]} below {node.Name}")} is a group of the mission database with geometry or a transform{(full == origin.ModelNode ? " where it stands now" : "")}; {(into is null ? "at the top" : $"under {parent!.Name}")} the build would delete it and its objects would join the world, so move its objects instead.");
            }, token, [.. notes, into != null ? $"{node.Name} moves with {parent!.Name} from now on."
                    : origin.Part ? $"{node.Name} becomes a root of {origin.ModelFile}, under each of the mission database's references to it."
                    : $"{node.Name} becomes a root of the mission database, which joins the world and its grid."], everyCopy: false);
            // Nothing to change is no edit: say where it already is rather than report a move.
            if (plan.Changes.Count == 0)
                throw new InvalidDataException(into != null ? $"{node.Name} is already under {parent!.Name}." : origin.Part ? $"{node.Name} is already a root of {origin.ModelFile}." : $"{node.Name} is already a root of the mission database.");
            return plan;
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
        // Attached again before the world is written, the node is no longer below its old parents when a NodeSetLighting on
        // one of them runs after its old AddChild (and below the new one only after every other line ran).
        if (Lighting(target, Ancestors(node)).Values.FirstOrDefault() is { Instruction: not null } lighting)
            throw new InvalidDataException($"{lighting.Instruction.Script} line {lighting.Instruction.Line} (NodeSetLighting) lights everything below {lighting.On.Name}, so {node.Name}, attached elsewhere at the end, would be lit differently; move it in the scripts directly.");
        var attached = origin.Named.Where(n => n.Command == "AddChild").ToList();
        // The scripts that created and placed the object change (each refused when another mission also runs it), and the
        // world script attaches it anew.
        if (TransformCommands.Where(origin.Writers.ContainsKey).Select(c => origin.Writers[c]).FirstOrDefault(w => !w.Script.Equals(created.Script, StringComparison.OrdinalIgnoreCase)) is { } elsewhere)
            throw new InvalidDataException($"{elsewhere.Script} line {elsewhere.Line} ({elsewhere.Command}) sets {node.Name}'s transform outside {created.Script}, which created it; move it in the scripts directly.");
        ScriptEdits edits = new(target, token);
        foreach (var instruction in attached) edits[instruction.Script].Comment(instruction);
        var edit = edits[created.Script];
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
                edit.Set(origin.Writers.GetValueOrDefault("Object3DTranslate"), "Object3DTranslate", ObjectTransform.SnapPosition(Vector3.Transform(stored.Position, q)), Vector3.Zero, anchor, stored.Position);
            else if (Near(basis * Matrix4x4.Transpose(basis), Matrix4x4.Identity) && basis.GetDeterminant() > 0)
            {
                var turned = ObjectTransform.FromMatrix(new ObjectTransform(Vector3.Zero, stored.RotationDegrees, Vector3.One).Matrix() * basis).RotationDegrees;
                edit.Set(origin.Writers.GetValueOrDefault("Object3DTranslate"), "Object3DTranslate", ObjectTransform.SnapPosition(Vector3.Transform(stored.Position, q)), Vector3.Zero, anchor, stored.Position);
                edit.Set(origin.Writers.GetValueOrDefault("Object3DRotate"), "Object3DRotate", ObjectTransform.Snap(turned, angles: true), Vector3.Zero, anchor, stored.RotationDegrees);
            }
            else
            {
                // A scaled or mirrored parent change: the transform is decomposed whole. Scripts hold translation, rotation and
                // scale only, so a parent whose scale is not uniform, turned against the node, would shear it.
                var exact = WorldMatrix(node) * inverse;
                var local = ObjectTransform.FromMatrix(exact);
                if (HasShear(exact))
                    throw new InvalidDataException($"Under {parent.Name}, {node.Name} would need a sheared or flattened transform (a parent whose scale is not uniform, turned against it, or a zero scale), which the script's Object3DTranslate, Object3DRotate and Object3DScale cannot hold; choose another parent or change the scales first.");
                edit.Set(origin.Writers.GetValueOrDefault("Object3DTranslate"), "Object3DTranslate", local.Position, Vector3.Zero, anchor);
                edit.Set(origin.Writers.GetValueOrDefault("Object3DRotate"), "Object3DRotate", ObjectTransform.Snap(local.RotationDegrees, angles: true), Vector3.Zero, anchor);
                edit.Set(origin.Writers.GetValueOrDefault("Object3DScale"), "Object3DScale", ObjectTransform.Snap(local.Scale, angles: false), Vector3.One, anchor);
            }
        }
        edits[SourceBuilder.WorldScript(target.Mission)].InsertBeforeWrite([["FindNode", parent.Name], ["AddChild", node.Name]], target.Write);
        return new(label, edits.Changes(), $"{created.Script} line {created.Line}", notes);
    }
    /// <summary>The NodeSetLighting instructions run on <paramref name="nodes"/> (each lights every model below its node as the build has it then), with the node each ran on.</summary>
    private static Dictionary<(string Script, int Line), (SourceInstruction Instruction, WorldNode On)> Lighting(SourceObjectTarget target, IEnumerable<WorldNode> nodes)
    {
        Dictionary<(string Script, int Line), (SourceInstruction Instruction, WorldNode On)> lit = [];
        foreach (var n in nodes)
            if (target.Provenance.TryGetValue(n, out var p))
                foreach (var i in p.Applied.Where(i => i.Command == "NodeSetLighting")) lit.TryAdd((i.Script.ToLowerInvariant(), i.Line), (i, n));
        return lit;
    }
    /// <summary>The nearest ancestor along first parents that <paramref name="inFile"/> does not hold: where a copy of the node's file stands.</summary>
    private static WorldNode? OutsideFile(WorldNode node, Func<WorldNode, bool> inFile)
    {
        var at = node.Parents.FirstOrDefault();
        for (int depth = 0; at != null && inFile(at) && depth <= GltfDocument.MaximumDepth; depth++) at = at.Parents.FirstOrDefault();
        return at;
    }
    /// <summary>
    /// Node <paramref name="index"/> of a mission database file and the groups below it through groups, when the build reaches
    /// it from the file's top through groups (<paramref name="topReached"/>: the file's top is reached, as the database's is
    /// and a part's is when its reference is): the build deletes those with the database, and their objects join the world.
    /// </summary>
    private static IEnumerable<int> GroupsReached(JsonObject root, int index, bool topReached)
    {
        var nodes = (JsonArray)root["nodes"]!;
        bool Group(int i) => ((nodes[i] as JsonObject)?["extras"] as JsonObject)?[WorldGltf.Key] is JsonObject engine && engine["group"] is JsonValue value
            && (value.TryGetValue(out bool flag) ? flag : value.TryGetValue(out double number) && number == 1);
        if (!topReached || !Group(index)) yield break;
        int depth = 0;
        for (int? at = GltfNodeEdits.Parent(root, index); at is int i; at = GltfNodeEdits.Parent(root, i))
            if (!Group(i) || ++depth > GltfDocument.MaximumDepth) yield break;
        Stack<int> pending = new([index]);
        while (pending.TryPop(out int group))
        {
            yield return group;
            foreach (var child in (nodes[group] as JsonObject)?["children"] as JsonArray ?? [])
                if (child is JsonValue v && v.TryGetValue(out int c) && c >= 0 && c < nodes.Count && Group(c)) pending.Push(c);
        }
    }
    /// <summary>Whether a group can be deleted with the database, its objects keeping their places: no geometry, no level of detail, no transform.</summary>
    private static bool EmptyGroup(JsonObject group)
    {
        var engine = (group["extras"] as JsonObject)?[WorldGltf.Key] as JsonObject;
        return group["mesh"] == null && engine?["model"] == null && !(engine?["class"] is JsonValue kind && kind.TryGetValue(out string? text) && text == "lod")
            && GltfNodeEdits.Local(group).IsIdentity;
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
        if (!commands.Contains(created.Command)) throw new InvalidDataException($"{target.Node.Name} was made by {created.Command} ({created.Script} line {created.Line}); here only objects made by {string.Join(" or ", commands)} can be {verb}.");
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
        HashSet<WorldNode> own = new(nodes, ReferenceEqualityComparer.Instance), below = new(Subtree(target.Node), ReferenceEqualityComparer.Instance);
        // The node itself only when the caller asks (a copy refuses its own instructions elsewhere, see PlanDuplicate).
        foreach (var n in WithCopies(target, own).Where(n => own.Contains(n) || !ReferenceEquals(n, target.Node)))
            if (target.Provenance.TryGetValue(n, out var p) && p.Applied.Concat(p.Named).FirstOrDefault(u => !skip.Any(h => Same(h, u))) is { } user)
                throw new InvalidDataException($"{user.Script} line {user.Line} ({user.Command}) acts on {n.Name}{(ReferenceEquals(n, target.Node) ? "" : below.Contains(n) ? $", which is below {name}" : $", which {p.ModelFile} holds below {name} (in another copy of it, or where a script moved it)")}; {name} cannot be {verb} until that instruction changes.");
    }
    /// <summary>An argument as one script token, refused when the tokenizer would read it otherwise.</summary>
    private static string Token(string value) => GameGenScriptText.WriteLine(["X", value]) is not null && value.Length > 0 ? value : throw new InvalidDataException($"'{value}' cannot be written as one script token.");
    /// <summary>
    /// The nodes a change of a glTF node's subtree reaches: its built subtree, and every built node made from the node's
    /// subtree in its file, wherever a script put it (gamegen's morfUtil.gw detaches morph LODs from their parents).
    /// </summary>
    private static IEnumerable<WorldNode> FileSubtree(SourceObjectTarget target, WorldNode node, WorldNodeProvenance origin, CancellationToken token)
    {
        var built = Subtree(node).ToList();
        if (origin.ModelFile is not { } file || target.Workspace.Read(file, token) is not { } bytes) return built;
        JsonObject? root;
        try { root = JsonNode.Parse(bytes, documentOptions: new() { MaxDepth = 256 }) as JsonObject; }
        catch (JsonException) { return built; }
        if (root?["nodes"] is not JsonArray nodes || origin.ModelNode < 0 || origin.ModelNode >= nodes.Count) return built;
        var indices = GltfNodeEdits.Descendants(root, origin.ModelNode);
        return built.Concat(target.Provenance.Where(p => indices.Contains(p.Value.ModelNode) && string.Equals(p.Value.ModelFile, file, StringComparison.OrdinalIgnoreCase)).Select(p => p.Key))
            .Distinct(ReferenceEqualityComparer.Instance).Cast<WorldNode>();
    }
    /// <summary>
    /// The script instructions that act on other nodes in <paramref name="after"/> than in <paramref name="before"/> (the
    /// first three, and how many more), or null: a move or copy in a glTF file changes the order the build makes nodes in,
    /// and a script's lookups (FindNode takes the newest node of a name, FindSubNode searches below the current node) can
    /// then find others. Nodes the build freed again count too (<see cref="SourceWorldBuild.Freed"/>). Nodes are identified
    /// by the load and glTF node they come from (a node its file places under several parents by its place in that
    /// instance, whichever copy the build read), with the node that references their file, or by the instruction that
    /// created them; both builds must number the files' nodes alike (no node removed), as a move or a copy leaves them.
    /// </summary>
    public static string? TargetChange(SourceWorldBuild before, SourceWorldBuild after)
    {
        TargetIdentities identities = new();
        var was = identities.Targets(before); var now = identities.Targets(after);
        List<string> changed = []; int count = 0;
        // Both ways: an instruction that found nothing before may find a node now (a copy named as a script looks up).
        foreach (var (script, line) in was.Keys.Union(now.Keys).OrderBy(k => k.Script, StringComparer.Ordinal).ThenBy(k => k.Line))
        {
            var nodes = was.GetValueOrDefault((script, line)) ?? [];
            var then = now.GetValueOrDefault((script, line)) ?? [];
            if (!nodes.SequenceEqual(then) && ++count <= 3)
                changed.Add($"{TargetIdentities.Short(script, 160)} line {line} would act on {identities.Describe(then)} instead of {identities.Describe(nodes)}");
        }
        return count == 0 ? null
            : $"{string.Join("; ", changed)}{(count > 3 ? $"; and {count - 3} more instructions" : "")}: the build finds nodes by name, and this change alters which one it finds. Make the change in Blender or in the scripts.";
    }
    /// <summary>
    /// Numbers for the nodes script instructions act on, shared by the builds compared: nodes made from the same source get
    /// the same number. Each source, referencing node and place in an instance is numbered once, so a build costs one pass
    /// over the nodes instructions acted on.
    /// </summary>
    private sealed class TargetIdentities
    {
        private readonly record struct Key(int Kind, string? File, string? Name, int Index, int Place, string? Script, int Line, int Within);
        private readonly Dictionary<Key, int> numbers = [];
        private readonly Dictionary<object, int> known = new(ReferenceEqualityComparer.Instance);
        /// <summary>A source of each number, to describe it (null for a place in an instance).</summary>
        private readonly List<WorldNodeProvenance?> sources = [];

        public Dictionary<(string Script, int Line), List<int>> Targets(SourceWorldBuild build)
        {
            Dictionary<(string, int), List<int>> targets = [];
            foreach (var origin in build.Provenance.Values.Concat(build.Freed))
            {
                if (origin.Applied.Count == 0 && origin.Named.Count == 0) continue;
                int identity = Number(origin, 0);
                foreach (var instruction in origin.Applied.Concat(origin.Named))
                {
                    if (!targets.TryGetValue((instruction.Script, instruction.Line), out var list)) targets[(instruction.Script, instruction.Line)] = list = [];
                    list.Add(identity);
                }
            }
            foreach (var list in targets.Values) list.Sort();
            return targets;
        }
        /// <summary>
        /// A glTF node by its file, load and index (in an instance, by its place there) and, for a file another node
        /// references, by that node too, so copies of one file are told apart; a terrain piece by its recipe, surface and
        /// cell; anything else by the instruction that created it.
        /// </summary>
        private int Number(WorldNodeProvenance origin, int depth)
        {
            if (known.TryGetValue(origin, out int number)) return number;
            Key key = origin.Terrain is { } recipe ? new(3, recipe, origin.TerrainSurface, origin.TerrainCell.Column, origin.TerrainCell.Row, origin.Load?.Script, origin.Load?.Line ?? 0, -1)
                : origin.ModelFile is { } file ? new(1, file, origin.ModelNodeName, origin.Instance == null ? origin.ModelNode : -1, origin.Instance is { } place ? Place(place, 0) : -1,
                    origin.Load?.Script, origin.Load?.Line ?? 0, origin.ReferencedBy is { } by && depth < GltfDocument.MaximumDepth ? Number(by, depth + 1) : -1)
                : origin.Created is { } created ? new(2, null, null, 0, -1, created.Script, created.Line, -1) : default;
            return known[origin] = Intern(key, origin);
        }
        private int Place(InstancePlace place, int depth)
        {
            if (known.TryGetValue(place, out int number)) return number;
            Key key = place.Parent is { } parent && depth < GltfDocument.MaximumDepth ? new(5, null, null, place.Child, Place(parent, depth + 1), null, 0, -1) : new(4, null, null, place.Number, -1, null, 0, -1);
            return known[place] = Intern(key, null);
        }
        private int Intern(Key key, WorldNodeProvenance? source)
        {
            if (numbers.TryGetValue(key, out int number)) return number;
            numbers[key] = number = sources.Count; sources.Add(source);
            return number;
        }
        /// <summary>Up to three of the nodes in words, and how many more.</summary>
        public string Describe(IReadOnlyList<int> nodes) => nodes.Count == 0 ? "no node"
            : string.Join(", ", nodes.Take(3).Select(n => sources[n] is { } origin ? Describe(origin, 0) : "a node")) + (nodes.Count > 3 ? $" and {nodes.Count - 3} more" : "");
        private static string Describe(WorldNodeProvenance origin, int depth)
        {
            if (origin.Terrain is { } recipe) return $"a piece of {Short(recipe, 160)} (surface {Short(origin.TerrainSurface, 64)}, cell {origin.TerrainCell.Column}, {origin.TerrainCell.Row})";
            if (origin.ModelFile is not { } file) return origin.Created is { } created ? $"the node {Short(created.Script, 160)} line {created.Line} made" : "a node";
            string at = origin.Instance is { } place ? $"instance {Short(place.ToString(), 64)}" : $"node {origin.ModelNode}";
            string load = origin.Load is { } l ? $", loaded at {Short(l.Script, 160)} line {l.Line}" : "";
            // The referencing node, and only a mark for those referencing it in turn.
            string within = origin.ReferencedBy is not { } by ? "" : depth == 0 ? $", in {Describe(by, 1)}" : ", in …";
            return $"{(string.IsNullOrEmpty(origin.ModelNodeName) ? "an unnamed node" : Short(origin.ModelNodeName, 64))} ({Short(file, 160)} {at}{load}{within})";
        }
        internal static string Short(string? text, int length) => text == null ? "" : text.Length <= length ? text : text[..length] + "…";
    }
    /// <summary>
    /// The provenance of every node made from <paramref name="origin"/>'s glTF node (with itself), from all of a world's: each
    /// copy of a part of the mission database, each load of a model file. An edit of the file's node changes all of them.
    /// </summary>
    public static IEnumerable<WorldNodeProvenance> CopiesOf(WorldNodeProvenance origin, IEnumerable<WorldNodeProvenance> all) =>
        origin.ModelFile == null ? [origin]
            : all.Where(p => p.ModelNode == origin.ModelNode && string.Equals(p.ModelFile, origin.ModelFile, StringComparison.OrdinalIgnoreCase));
    /// <summary>
    /// Which copy of its file a node is in: the chain of references that copied the file (a part the mission database
    /// references several times, a model file several nodes reference), as text that compares across rebuilds; empty for a
    /// node of a file read once.
    /// </summary>
    public static string CopyKey(WorldNodeProvenance origin)
    {
        StringBuilder key = new();
        int depth = 0;
        for (var by = origin.ReferencedBy; by != null && depth++ < 64; by = by.ReferencedBy)
            key.Append(by.ModelFile?.ToLowerInvariant()).Append('#').Append(by.ModelNode).Append(by.Load is { } load ? $"@{load.Script.ToLowerInvariant()}:{load.Line}" : "").Append('/');
        return key.ToString();
    }
    /// <summary>
    /// <paramref name="nodes"/> with every other copy of their part nodes: an edit of a part's file changes each copy the
    /// mission database references, so what scripts do to any copy counts.
    /// </summary>
    private static IEnumerable<WorldNode> WithCopies(SourceObjectTarget target, IEnumerable<WorldNode> nodes)
    {
        var list = nodes.ToList();
        HashSet<(string, int)> parts = [.. list.Select(n => target.Provenance.TryGetValue(n, out var p) && p.Part && p.ModelFile != null ? (p.ModelFile.ToLowerInvariant(), p.ModelNode) : default)
            .Where(k => k.Item1 != null)];
        if (parts.Count == 0) return list;
        return list.Concat(target.Provenance.Where(kv => kv.Value.Part && kv.Value.ModelFile != null && parts.Contains((kv.Value.ModelFile.ToLowerInvariant(), kv.Value.ModelNode))).Select(kv => kv.Key))
            .Distinct(ReferenceEqualityComparer.Instance).Cast<WorldNode>();
    }
    private static IEnumerable<WorldNode> Ancestors(WorldNode node)
    {
        HashSet<WorldNode> seen = new(ReferenceEqualityComparer.Instance); Stack<WorldNode> pending = new(node.Parents);
        while (pending.TryPop(out var parent)) if (seen.Add(parent)) { yield return parent; foreach (var up in parent.Parents) pending.Push(up); }
    }
    /// <summary>Whether two world placements are the same, allowing for the rounding of composed transforms.</summary>
    private static bool SamePlace(Matrix4x4 a, Matrix4x4 b)
    {
        // Relative to the transform's own size: the basis (rotation and scale, however small) and the translation (at least
        // 1e-5 of a unit) apart.
        float Largest(int from, int to, float least) { float m = least; for (int row = from; row < to; row++) for (int column = 0; column < 3; column++) m = MathF.Max(m, MathF.Max(MathF.Abs(a[row, column]), MathF.Abs(b[row, column]))); return m; }
        float basis = 1e-4f * Largest(0, 3, 0), move = 1e-5f * Largest(3, 4, 1);
        for (int row = 0; row < 4; row++)
            for (int column = 0; column < 4; column++)
                if (MathF.Abs(a[row, column] - b[row, column]) > (row == 3 && column < 3 ? move : basis)) return false;
        return true;
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
    /// doubles), so only the neighbouring float counts as the same.
    /// </summary>
    private static bool Differs(Vector3 requested, Vector3 shown) => Enumerable.Range(0, 3).Any(i => Differs(requested[i], shown[i]));
    // NaN differs from everything, so it reaches the checks.
    private static bool Differs(float requested, float shown) => !(requested <= MathF.BitIncrement(shown) && requested >= MathF.BitDecrement(shown));
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

    /// <summary>A change of a glTF node's own values (its transform, its extras), made alike in every copy of an instance holding it.</summary>
    private static SourceEditPlan GltfEdit(SourceWorkspace workspace, WorldNodeProvenance origin, string label, Action<JsonObject> change, CancellationToken token, IReadOnlyList<string>? notes = null) =>
        GltfFile(workspace, origin, label, (document, copies) =>
        {
            var nodes = (JsonArray)document["nodes"]!;
            var node = (JsonObject)nodes[copies[0]]!;
            change(node);
            var after = Own(node);
            foreach (int c in copies.Skip(1))
            {
                var copy = (JsonObject)nodes[c]!;
                foreach (string key in copy.Select(p => p.Key).Where(k => k is not ("children" or "name")).ToList()) copy.Remove(key);
                foreach (var (key, value) in after) copy[key] = value?.DeepClone();
            }
        }, token, notes ?? []);
    /// <summary>Whether a glTF node carries an instance mark (it is one of the copies of a node the file places under several parents).</summary>
    private static bool Marked(JsonObject node) => ((node["extras"] as JsonObject)?[WorldGltf.Key] as JsonObject)?["instance"] != null;
    /// <summary>A glTF node's own values with those of its descendants, as nested children: what makes copies of an instance alike.</summary>
    private static JsonObject Shape(JsonArray nodes, int index, int depth)
    {
        if (depth > GltfDocument.MaximumDepth) throw new InvalidDataException("The node hierarchy is cyclic or too deep.");
        var node = (JsonObject)nodes[index]!;
        var shape = Own(node);
        shape["children"] = new JsonArray([.. (node["children"] as JsonArray ?? []).Select(c => (JsonNode?)Shape(nodes, c!.GetValue<int>(), depth + 1))]);
        return shape;
    }
    /// <summary>A glTF node's own values: all but its children (indices within its copy) and its name (an editor may suffix a copy's).</summary>
    private static JsonObject Own(JsonObject node)
    {
        JsonObject own = [];
        foreach (var (key, value) in node) if (key is not ("children" or "name")) own[key] = value?.DeepClone();
        return own;
    }
    /// <summary>
    /// A change of the glTF file a node came from: <paramref name="change"/> gets the document and the node's index, followed,
    /// when an instance holds the node (a node the file places under several parents, written as copies with one mark, or a
    /// node inside one), by the nodes standing for it in the instance's other copies, which it changes alike: the build reads
    /// the first copy, and the others would come back once it is removed or moves later in the file. Without
    /// <paramref name="everyCopy"/> (a move, which keeps one place) such a node is refused.
    /// </summary>
    private static SourceEditPlan GltfFile(SourceWorkspace workspace, WorldNodeProvenance origin, string label, Action<JsonObject, IReadOnlyList<int>> change, CancellationToken token, IReadOnlyList<string> notes, bool everyCopy = true)
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
        var copies = GltfNodeEdits.InstanceCopies(document, origin.ModelNode);
        string shown = origin.ModelNodeName is { Length: > 0 } named ? named : $"Node {origin.ModelNode}";
        if (copies.Count > 1 && !everyCopy)
            throw new InvalidDataException($"{shown} is {(Marked(node) ? "" : "inside ")}a node {file} places under several parents (an instance); move it in Blender instead.");
        // The build reads the first copy: another that differs would keep values (or parts) nobody sees, and its nodes would
        // not stand for the first's.
        if (copies.Count > 1 && Shape(nodes, copies[0], 0) is var shape && copies.Skip(1).Any(c => !JsonNode.DeepEquals(Shape(nodes, c, 0), shape)))
            throw new InvalidDataException($"The copies of {shown} in the instance {file} places under several parents differ; make them alike in Blender first.");
        change(document, copies);
        // zStudio and Blender write glTF indented or minified; keep the file's style.
        bool indented = bytes.AsSpan(0, Math.Min(bytes.Length, 4096)).Contains((byte)'\n');
        byte[] content = Encoding.UTF8.GetBytes(root.ToJsonString(new JsonSerializerOptions { WriteIndented = indented }));
        if (content.AsSpan().SequenceEqual(bytes)) return new(label, [], $"{file} node {origin.ModelNode}", notes);
        List<string> all = [.. notes];
        if (copies.Count > 1) all.Add($"{file} holds {shown} in an instance it places under several parents: the change applies to each of its {copies.Count} copies.");
        if (!origin.Database) all.Add($"{file} is a model file: the change applies wherever it is loaded.");
        else if (origin.Part) all.Add($"{file} is a part of the mission database: the change applies to every copy of it the database references.");
        return new(label, [(file, content)], $"{file} node {origin.ModelNode}", all);
    }
    private static JsonArray Array(params float[] values) => new(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());
    /// <summary>A scale of 0 flattens the object and leaves no rotation to read back, so it could not be turned or scaled again.</summary>
    private static void CheckScale(Vector3 scale)
    {
        // Below 1e-5, an axis read back after the build could fall under the 1e-6 a flattened axis counts at (HasShear).
        if (MathF.Abs(scale.X) < 1e-5f || MathF.Abs(scale.Y) < 1e-5f || MathF.Abs(scale.Z) < 1e-5f)
            throw new InvalidDataException("A scale of 0 (or below 0.00001) flattens the object for good; use a larger scale.");
    }
    /// <summary>
    /// Whether a transform's basis cannot be shown as rotation and scale: a shear (its axes, the rows, are not perpendicular;
    /// translation, rotation and scale never make one, whatever the angles, while a parent whose scale is not uniform,
    /// turned against its child, does), or a flattened axis, which leaves no rotation to read back.
    /// </summary>
    private static bool HasShear(Matrix4x4 m)
    {
        Vector3 x = new(m.M11, m.M12, m.M13), y = new(m.M21, m.M22, m.M23), z = new(m.M31, m.M32, m.M33);
        if (x.Length() < 1e-6f || y.Length() < 1e-6f || z.Length() < 1e-6f) return true;
        static bool Skewed(Vector3 a, Vector3 b) => MathF.Abs(Vector3.Dot(a, b)) > 1e-4f * a.Length() * b.Length();
        return Skewed(x, y) || Skewed(y, z) || Skewed(x, z);
    }
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
            var others = MissionsRunning(workspace, script, token).Where(m => !m.Equals(mission, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (others.Length > 0) throw new InvalidDataException($"{script} also runs in {string.Join(", ", others.Take(6))}{(others.Length > 6 ? $" and {others.Length - 6} more" : "")}; editing it would change those missions too. Edit it in the script directly.");
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

    /// <summary>One edit of several scripts: a <see cref="ScriptEdit"/> per script, each checked (and refused when other missions run it) on its own.</summary>
    private sealed class ScriptEdits(SourceObjectTarget target, CancellationToken token)
    {
        private readonly Dictionary<string, ScriptEdit> edits = new(StringComparer.OrdinalIgnoreCase);
        public ScriptEdit this[string script] => edits.TryGetValue(script, out var edit) ? edit : edits[script] = new(target.Workspace, script, target.Executions, token, target.Mission);
        public IReadOnlyList<(string Relative, byte[] Content)> Changes() => [.. edits.Values.SelectMany(e => e.Changes())];
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
